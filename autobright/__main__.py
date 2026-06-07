import argparse

from .brightness_app import BrightnessDimmerApp
from .sensor_manager import YAPI, YoctoImportError


def build_parser():
    parser = argparse.ArgumentParser(description='Monitor brightness dimmer with Yoctopuce ambient light sensor and Windows monitor control.')
    parser.add_argument('--min-lux', type=float, default=None, help='Lux value that corresponds to minimum brightness')
    parser.add_argument('--max-lux', type=float, default=None, help='Lux value that corresponds to maximum brightness')
    parser.add_argument('--min-brightness', type=int, default=None, help='Minimum target brightness percentage')
    parser.add_argument('--max-brightness', type=int, default=None, help='Maximum target brightness percentage')
    parser.add_argument('--remote-hub', type=str, default=None, help='Remote YoctoHub address or usb for local USB devices')
    parser.add_argument(
        '--update-rate',
        '--interval',
        dest='interval',
        type=float,
        default=None,
        help='Update interval in seconds (alias: --interval)',
    )
    parser.add_argument('--show-lux', action='store_true', default=None, help='Display current lux value in the GUI')
    parser.add_argument('--minimize-to-tray', action='store_true', default=None, help='Minimize the app to the notification area on close')
    parser.add_argument('--start-minimized', action='store_true', default=None, help='Start the app minimized to the notification area')
    parser.add_argument('--debug', action='store_true', help='Enable debug logging')
    return parser


def main():
    args = build_parser().parse_args()
    if YAPI is None:
        print('Yoctopuce Python library is not installed.')
        print('Install it from the Yoctopuce website or add the yocto_api.py and yocto_lightsensor.py modules to your Python path.')
        print(f'Import error: {YoctoImportError}')
    BrightnessDimmerApp(args)


if __name__ == '__main__':
    main()
