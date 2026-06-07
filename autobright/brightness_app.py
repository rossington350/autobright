import json
import os
from pathlib import Path
import threading
import time
import traceback

import tkinter as tk
from tkinter import ttk
from tkinter import messagebox

from .monitor_manager import MonitorManager
from .sensor_manager import YoctoSensorManager
from .tray_icon import TrayIcon

APP_NAME = 'BrightnessDimmer'
SETTINGS_FILENAME = 'settings.json'


class BrightnessDimmerApp:
    DEFAULT_MIN_LUX = 50.0
    DEFAULT_MAX_LUX = 300.0
    DEFAULT_MIN_BRIGHTNESS = 20
    DEFAULT_MAX_BRIGHTNESS = 100
    DEFAULT_REFRESH_MS = 1.0
    RETAIN_MONITOR_TIMEOUT = 10.0

    def __init__(self, args):
        self.args = args
        self.settings_path = self._get_settings_path()
        self.settings = self._load_settings()
        self.monitor_manager = MonitorManager()
        self.sensor_manager = YoctoSensorManager(hub_url=args.remote_hub or self.settings.get('remote_hub', 'usb'), debug=args.debug)
        self.running = True
        self.monitor_success_count = 0
        self.monitor_failure_count = 0
        self.last_good_monitors = []
        self.last_good_monitors_time = 0.0
        self.state = {
            'current_lux': None,
            'target_brightness': None,
            'monitor_status': 'Starting...',
            'last_error': '',
            'monitor_list': [],
        }

        self.min_lux = args.min_lux if args.min_lux is not None else self.settings.get('min_lux', self.DEFAULT_MIN_LUX)
        self.max_lux = args.max_lux if args.max_lux is not None else self.settings.get('max_lux', self.DEFAULT_MAX_LUX)
        self.min_brightness = args.min_brightness if args.min_brightness is not None else self.settings.get('min_brightness', self.DEFAULT_MIN_BRIGHTNESS)
        self.max_brightness = args.max_brightness if args.max_brightness is not None else self.settings.get('max_brightness', self.DEFAULT_MAX_BRIGHTNESS)
        self.interval = args.interval if args.interval is not None else self.settings.get('interval', self.DEFAULT_REFRESH_MS)
        self.show_lux = args.show_lux if args.show_lux is not None else self.settings.get('show_lux', False)
        self.minimize_to_tray = args.minimize_to_tray if args.minimize_to_tray is not None else self.settings.get('minimize_to_tray', False)
        self.start_minimized = args.start_minimized if args.start_minimized is not None else False
        self.tray_icon = None

        if self.min_lux >= self.max_lux:
            self.max_lux = self.min_lux + 1.0
        if self.min_brightness >= self.max_brightness:
            self.max_brightness = min(100, self.min_brightness + 1)
        if self.interval <= 0:
            self.interval = self.DEFAULT_REFRESH_MS

        self.root = tk.Tk()
        self.root.title('Brightness Dimmer')
        self.root.protocol('WM_DELETE_WINDOW', self._on_close)
        self._build_gui()
        self.root.update_idletasks()
        # restore window geometry if previously saved (widthxheight+x+y)
        geom = self.settings.get('window_geometry')
        if geom:
            try:
                self.root.geometry(geom)
            except Exception:
                pass
        # allow the window to be resized by the user

        if self.start_minimized:
            self._hide_to_tray()

        self.worker_thread = threading.Thread(target=self._worker_loop, daemon=True)
        self.worker_thread.start()
        self.root.after(200, self._refresh_ui)
        self.root.mainloop()

    def _get_settings_path(self):
        base_dir = Path(os.getenv('APPDATA') or Path.home()) / APP_NAME
        base_dir.mkdir(parents=True, exist_ok=True)
        return base_dir / SETTINGS_FILENAME

    def _load_settings(self):
        try:
            if self.settings_path.exists():
                with open(self.settings_path, 'r', encoding='utf-8') as settings_file:
                    return json.load(settings_file)
        except Exception:
            pass
        return {}

    def _save_settings(self):
        try:
            self.settings_path.parent.mkdir(parents=True, exist_ok=True)
            with open(self.settings_path, 'w', encoding='utf-8') as settings_file:
                json.dump(
                    {
                        'min_lux': self.min_lux,
                        'max_lux': self.max_lux,
                        'min_brightness': self.min_brightness,
                        'max_brightness': self.max_brightness,
                        'interval': self.interval,
                        'show_lux': self.show_lux,
                        'minimize_to_tray': self.minimize_to_tray,
                        'remote_hub': self.args.remote_hub or self.settings.get('remote_hub', 'usb'),
                        'window_geometry': self.root.winfo_geometry(),
                    },
                    settings_file,
                    indent=2,
                )
            self.state['monitor_status'] = 'Settings saved.'
        except Exception as exc:
            self.state['last_error'] = f'Failed to save settings: {exc}'


    def _hide_to_tray(self):
        if self.tray_icon is None:
            self.tray_icon = TrayIcon(self.root, 'Brightness Dimmer', self._tray_action)
            self.tray_icon.show()
        self.root.withdraw()
        self.state['monitor_status'] = 'Hidden to tray. Double-click tray icon or use menu to restore.'

    def _restore_from_tray(self):
        if self.tray_icon is not None:
            self.tray_icon.cleanup()
            self.tray_icon = None
        self.root.deiconify()
        self.root.after(0, self.root.lift)
        self.state['monitor_status'] = 'Restored from tray.'

    def _tray_action(self, action):
        if action == 'restore':
            self._restore_from_tray()
        elif action == 'quit':
            self.running = False
            self._destroy_tray_icon()
            self.root.destroy()

    def _destroy_tray_icon(self):
        if self.tray_icon is not None:
            self.tray_icon.cleanup()
            self.tray_icon = None

    def _build_gui(self):
        frame = ttk.Frame(self.root, padding='12')
        frame.grid(row=0, column=0, sticky='nsew')
        self.root.columnconfigure(0, weight=1)
        self.root.rowconfigure(0, weight=1)

        ttk.Label(frame, text='Min lux:').grid(row=0, column=0, sticky='w')
        self.min_lux_var = tk.StringVar(value=str(self.min_lux))
        self.min_lux_entry = ttk.Entry(frame, textvariable=self.min_lux_var, width=12)
        self.min_lux_entry.grid(row=0, column=1, sticky='w')

        ttk.Label(frame, text='Max lux:').grid(row=1, column=0, sticky='w')
        self.max_lux_var = tk.StringVar(value=str(self.max_lux))
        self.max_lux_entry = ttk.Entry(frame, textvariable=self.max_lux_var, width=12)
        self.max_lux_entry.grid(row=1, column=1, sticky='w')

        ttk.Label(frame, text='Min brightness (%):').grid(row=2, column=0, sticky='w')
        self.min_brightness_var = tk.StringVar(value=str(self.min_brightness))
        self.min_brightness_entry = ttk.Entry(frame, textvariable=self.min_brightness_var, width=12)
        self.min_brightness_entry.grid(row=2, column=1, sticky='w')

        ttk.Label(frame, text='Max brightness (%):').grid(row=3, column=0, sticky='w')
        self.max_brightness_var = tk.StringVar(value=str(self.max_brightness))
        self.max_brightness_entry = ttk.Entry(frame, textvariable=self.max_brightness_var, width=12)
        self.max_brightness_entry.grid(row=3, column=1, sticky='w')

        ttk.Label(frame, text='Update rate (s):').grid(row=4, column=0, sticky='w')
        self.interval_var = tk.StringVar(value=str(self.interval))
        self.interval_entry = ttk.Entry(frame, textvariable=self.interval_var, width=12)
        self.interval_entry.grid(row=4, column=1, sticky='w')

        self.tray_var = tk.BooleanVar(value=self.minimize_to_tray)
        ttk.Checkbutton(frame, text='Minimize to tray on close', variable=self.tray_var).grid(row=5, column=0, columnspan=2, sticky='w')
        # manual apply button (kept for convenience)
        ttk.Button(frame, text='Apply', command=self._apply_parameters).grid(row=6, column=0, columnspan=2, pady=(8, 10), sticky='ew')

        self.lux_label = ttk.Label(frame, text='Current lux: ?')
        self.lux_label.grid(row=7, column=0, columnspan=2, sticky='w')

        self.target_label = ttk.Label(frame, text='Target brightness: ?')
        self.target_label.grid(row=8, column=0, columnspan=2, sticky='w')

        # Use the existing monitor listbox to show status messages and monitor info
        self.monitor_listbox = tk.Listbox(frame, width=60, height=8)
        self.monitor_listbox.grid(row=9, column=0, columnspan=2, pady=(8, 0), sticky='ew')

        button_frame = ttk.Frame(frame)
        button_frame.grid(row=11, column=0, columnspan=2, sticky='ew', pady=(8, 0))
        ttk.Button(button_frame, text='Refresh monitors', command=self._refresh_monitors).pack(side='left')
        ttk.Button(button_frame, text='Reconnect sensor', command=self._reconnect_sensor).pack(side='left', padx=(8, 0))
        ttk.Button(button_frame, text='Hide to tray', command=self._hide_to_tray).pack(side='left', padx=(8, 0))

        for idx in range(2):
            frame.columnconfigure(idx, weight=1)

    def _apply_parameters(self):
        try:
            min_lux = float(self.min_lux_var.get())
            max_lux = float(self.max_lux_var.get())
            min_brightness = int(self.min_brightness_var.get())
            max_brightness = int(self.max_brightness_var.get())
            interval = float(self.interval_var.get())
        except ValueError:
            messagebox.showerror(
                'Input error',
                'Please enter valid numeric values for min/max lux, brightness, and update rate.',
            )
            return

        if min_lux >= max_lux:
            messagebox.showerror('Input error', 'Minimum lux must be lower than maximum lux.')
            return
        if not (0 <= min_brightness <= 100 and 0 <= max_brightness <= 100):
            messagebox.showerror('Input error', 'Brightness values must be between 0 and 100.')
            return
        if min_brightness >= max_brightness:
            messagebox.showerror('Input error', 'Minimum brightness must be lower than maximum brightness.')
            return
        if interval <= 0:
            messagebox.showerror('Input error', 'Update rate must be greater than zero.')
            return

        self.min_lux = min_lux
        self.max_lux = max_lux
        self.min_brightness = min_brightness
        self.max_brightness = max_brightness
        self.interval = interval
        self.minimize_to_tray = self.tray_var.get()
        self._save_settings()
        self._update_status('Parameters updated.')

    def _refresh_monitors(self):
        self.state['monitor_status'] = 'Refreshing monitor list...'
        self.state['monitor_list'] = []
        self.monitor_manager.refresh_monitors()
        self.monitor_manager.refresh_monitors()

    def _reconnect_sensor(self):
        self.sensor_manager.sensor = None
        self.sensor_manager._api_ready = False
        self.state['last_error'] = 'Sensor reconnect requested.'

    def _update_status(self, message):
        self.state['monitor_status'] = message

    def _compute_brightness(self, lux_value):
        if lux_value is None:
            return None
        if lux_value <= self.min_lux:
            return self.min_brightness
        if lux_value >= self.max_lux:
            return self.max_brightness
        span_lux = self.max_lux - self.min_lux
        span_brightness = self.max_brightness - self.min_brightness
        if span_lux <= 0:
            return self.min_brightness
        result = self.min_brightness + (lux_value - self.min_lux) * span_brightness / span_lux
        return max(self.min_brightness, min(self.max_brightness, int(round(result))))

    def _percent_to_monitor_value(self, monitor, brightness_percent):
        if brightness_percent is None:
            return None
        percent = max(0, min(100, brightness_percent)) / 100.0
        if monitor.max_brightness <= monitor.min_brightness:
            return int(round(percent * 100))
        raw_value = monitor.min_brightness + percent * (monitor.max_brightness - monitor.min_brightness)
        return int(round(raw_value))

    def _monitor_value_to_percent(self, monitor, value):
        if monitor.max_brightness <= monitor.min_brightness:
            return value
        return int(round((value - monitor.min_brightness) / (monitor.max_brightness - monitor.min_brightness) * 100))

    def _worker_loop(self):
        while self.running:
            try:
                self.sensor_manager.tick()
                lux = self.sensor_manager.get_lux()
                target = self._compute_brightness(lux)
                monitors = self.monitor_manager.enumerate_monitors()
                if not monitors:
                    self.monitor_failure_count += 1
                    self.monitor_success_count = 0
                    if self.last_good_monitors and (time.time() - self.last_good_monitors_time) < self.RETAIN_MONITOR_TIMEOUT:
                        lines = []
                        for monitor in self.last_good_monitors:
                            name = monitor.name or 'Unnamed monitor'
                            current_pct = self._monitor_value_to_percent(monitor, monitor.cur_brightness)
                            lines.append(
                                f'{name}: last known current {current_pct}% (raw {monitor.cur_brightness}), hardware range {monitor.min_brightness}-{monitor.max_brightness}'
                            )
                        self.state['monitor_list'] = lines
                        timeout_left = int(self.RETAIN_MONITOR_TIMEOUT - (time.time() - self.last_good_monitors_time))
                        if timeout_left < 0:
                            timeout_left = 0
                        self.state['monitor_status'] = (
                            f'Temporary monitor loss detected. Retaining last known monitor list for {timeout_left}s while retrying...'
                        )
                        target_text = f' Target brightness would be {target}%' if target is not None else ''
                        self.state['monitor_status'] += target_text
                    else:
                        target_text = f' Target brightness would be {target}%' if target is not None else ''
                        self.state['monitor_status'] = (
                            f'No compatible monitors found. Ensure your monitor supports Windows DDC/CI brightness control. Waiting for reconnect...{target_text}'
                        )
                        self.state['monitor_list'] = []
                else:
                    self.monitor_success_count += 1
                    self.monitor_failure_count = 0
                    self.last_good_monitors = monitors
                    self.last_good_monitors_time = time.time()
                    lines = []
                    for monitor in monitors:
                        name = monitor.name or 'Unnamed monitor'
                        current_pct = self._monitor_value_to_percent(monitor, monitor.cur_brightness)
                        lines.append(
                            f'{name}: current {current_pct}% (raw {monitor.cur_brightness}), hardware range {monitor.min_brightness}-{monitor.max_brightness}'
                        )
                        if target is not None:
                            target_value = self._percent_to_monitor_value(monitor, target)
                            success = self.monitor_manager.set_brightness(monitor.handle, target_value)
                            if success:
                                lines.append(f'  set to {target}% (raw {target_value})')
                            else:
                                lines.append('  failed to set brightness')
                    if self.monitor_success_count >= 2:
                        self.state['monitor_list'] = lines
                        self.state['monitor_status'] = 'Monitors adjusted.'
                self.state['current_lux'] = lux
                self.state['target_brightness'] = target
                self.state['last_error'] = self.sensor_manager.last_error or ''
            except Exception as exc:
                self.state['last_error'] = f'Worker error: {exc}'
                if self.args.debug:
                    traceback.print_exc()
            time.sleep(self.interval)

    def _refresh_ui(self):
        status_text = self.state.get('monitor_status', '')
        if self.state.get('last_error'):
            status_text += ' | ' + self.state['last_error']

        if self.show_lux:
            lux_text = f'Current lux: {self.state['current_lux']:.2f}' if self.state['current_lux'] is not None else 'Current lux: n/a'
        else:
            lux_text = 'Current lux: hidden'
        self.lux_label.config(text=lux_text)

        target = self.state.get('target_brightness')
        self.target_label.config(text=f'Target brightness: {target}%' if target is not None else 'Target brightness: n/a')

        # write the status as the first line in the monitor listbox, then monitor info
        self.monitor_listbox.delete(0, tk.END)
        if status_text:
            self.monitor_listbox.insert(tk.END, status_text)
            self.monitor_listbox.insert(tk.END, '---')
        for line in self.state.get('monitor_list', []):
            self.monitor_listbox.insert(tk.END, line)

        if self.running:
            self.root.after(500, self._refresh_ui)

    def _on_close(self):
        if self.minimize_to_tray:
            self._hide_to_tray()
            return
        self.running = False
        self.monitor_manager.destroy_monitors()
        self._destroy_tray_icon()
        self.root.destroy()
