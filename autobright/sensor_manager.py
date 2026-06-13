import traceback

YAPI = None
YLightSensor = None
YLed = None
YColorLed = None
YRefParam = None
YAPI_SUCCESS = None
YoctoImportError = None

try:
    import yocto_api as _yocto_api
    import yocto_lightsensor as _yocto_ls
    import yocto_led as _yocto_led
    import yocto_colorled as _yocto_colorled
    YAPI = _yocto_api.YAPI
    YLightSensor = _yocto_ls.YLightSensor
    YLed = _yocto_led.YLed
    YColorLed = _yocto_colorled.YColorLed
    YRefParam = getattr(_yocto_api, 'YRefParam', None)
    YAPI_SUCCESS = getattr(_yocto_api, 'YAPI_SUCCESS', getattr(YAPI, 'SUCCESS', None))
except Exception:
    try:
        import yoctopuce.yocto_api as _yocto_api
        import yoctopuce.yocto_lightsensor as _yocto_ls
        import yoctopuce.yocto_led as _yocto_led
        import yoctopuce.yocto_colorled as _yocto_colorled
        YAPI = _yocto_api.YAPI
        YLightSensor = _yocto_ls.YLightSensor
        YLed = _yocto_led.YLed
        YColorLed = _yocto_colorled.YColorLed
        YRefParam = getattr(_yocto_api, 'YRefParam', None)
        YAPI_SUCCESS = getattr(_yocto_api, 'YAPI_SUCCESS', getattr(YAPI, 'SUCCESS', None))
    except Exception as exc:
        YAPI = None
        YLightSensor = None
        YLed = None
        YColorLed = None
        YRefParam = None
        YoctoImportError = exc


class YoctoSensorManager:
    def __init__(self, hub_url='usb', debug=False):
        self.hub_url = hub_url
        self.debug = debug
        self.sensor = None
        self.last_error = ''
        self._api_ready = False
        self._leds_configured = False

    def _debug(self, message):
        if self.debug:
            print('[Yocto]', message)

    def reset(self):
        """Force a full reconnect on the next tick (drops the sensor and hub registration)."""
        self.sensor = None
        self._api_ready = False
        self._leds_configured = False

    def _register_hub(self):
        if YAPI is None:
            self.last_error = f'Yocto API import error: {YoctoImportError}'
            return False

        self.last_error = ''
        try:
            err = YRefParam() if YRefParam else ''
            result = YAPI.RegisterHub(self.hub_url, err)
            success_code = YAPI_SUCCESS if YAPI_SUCCESS is not None else getattr(YAPI, 'SUCCESS', 0)
            if result != success_code:
                self.last_error = str(err.value) if hasattr(err, 'value') else str(err)
                return False
            self._api_ready = True
            return True
        except Exception as exc:
            self.last_error = f'RegisterHub failed: {exc}'
            return False

    def _update_device_list(self):
        if not self._api_ready:
            return False
        try:
            err = YRefParam() if YRefParam else ''
            result = YAPI.UpdateDeviceList(err)
            success_code = YAPI_SUCCESS if YAPI_SUCCESS is not None else getattr(YAPI, 'SUCCESS', 0)
            if result != success_code:
                self.last_error = str(err.value) if hasattr(err, 'value') else str(err)
                # Force re-registration on the next tick; the hub was likely lost.
                self._api_ready = False
                return False
            return True
        except Exception as exc:
            self.last_error = f'UpdateDeviceList failed: {exc}'
            self._api_ready = False
            return False

    def _disable_yocto_leds(self):
        if YLed is not None:
            try:
                led = YLed.FirstLed()
                while led is not None:
                    try:
                        if hasattr(led, 'set_power') and hasattr(YLed, 'POWER_OFF'):
                            led.set_power(YLed.POWER_OFF)
                        elif hasattr(led, 'set_luminosity'):
                            led.set_luminosity(0)
                    except Exception:
                        pass
                    try:
                        led = led.nextLed()
                    except Exception:
                        break
            except Exception:
                pass
        if YColorLed is not None:
            try:
                color_led = YColorLed.FirstColorLed()
                while color_led is not None:
                    try:
                        if hasattr(color_led, 'set_rgbColor'):
                            color_led.set_rgbColor(0)
                        elif hasattr(color_led, 'set_hslColor'):
                            color_led.set_hslColor(0)
                    except Exception:
                        pass
                    try:
                        color_led = color_led.nextColorLed()
                    except Exception:
                        break
            except Exception:
                pass

    def find_sensor(self):
        if not self._api_ready and not self._register_hub():
            return False
        try:
            self.sensor = YLightSensor.FirstLightSensor()
        except Exception as exc:
            self.sensor = None
            self.last_error = f'Unable to enumerate Yocto sensors: {exc}'
            return False

        if self.sensor is None or (hasattr(self.sensor, 'isOnline') and not self.sensor.isOnline()):
            self.sensor = None
            self.last_error = 'No Yocto light sensor found.'
            return False

        if hasattr(self.sensor, 'set_reportFrequency'):
            try:
                self.sensor.set_reportFrequency('60/m')
            except Exception:
                pass

        # The LED/beacon state is persisted to flash, so only write it once per
        # connection. Flash has a limited number of write cycles and find_sensor
        # can run on every reconnect.
        if not self._leds_configured and hasattr(self.sensor, 'get_module'):
            try:
                module = self.sensor.get_module()
                if module is not None:
                    if hasattr(module, 'set_luminosity'):
                        try:
                            module.set_luminosity(0)
                        except Exception:
                            pass
                    if hasattr(module, 'set_beacon') and hasattr(module, 'BEACON_OFF'):
                        try:
                            module.set_beacon(module.BEACON_OFF)
                        except Exception:
                            pass
                    if hasattr(module, 'saveToFlash'):
                        try:
                            module.saveToFlash()
                        except Exception:
                            pass
            except Exception:
                pass
            self._disable_yocto_leds()
            self._leds_configured = True

        self.last_error = ''
        return True

    def get_lux(self):
        if self.sensor is None:
            if not self.find_sensor():
                return None
        try:
            if hasattr(self.sensor, 'isOnline') and not self.sensor.isOnline():
                self.sensor = None
                return None
            if hasattr(self.sensor, 'get_currentValue'):
                return float(self.sensor.get_currentValue())
            raise AttributeError('Yocto light sensor has no get_currentValue method')
        except Exception as exc:
            self.last_error = f'Unable to read sensor value: {exc}'
            if self.debug:
                traceback.print_exc()
            self.sensor = None
            return None

    def tick(self):
        if not self._api_ready:
            self._register_hub()
        else:
            self._update_device_list()
