import traceback

YAPI = None
YLightSensor = None
YRefParam = None
YAPI_SUCCESS = None
YoctoImportError = None

try:
    import yocto_api as _yocto_api
    import yocto_lightsensor as _yocto_ls
    YAPI = _yocto_api.YAPI
    YLightSensor = _yocto_ls.YLightSensor
    YRefParam = getattr(_yocto_api, 'YRefParam', None)
    YAPI_SUCCESS = getattr(_yocto_api, 'YAPI_SUCCESS', getattr(YAPI, 'SUCCESS', None))
except Exception:
    try:
        import yoctopuce.yocto_api as _yocto_api
        import yoctopuce.yocto_lightsensor as _yocto_ls
        YAPI = _yocto_api.YAPI
        YLightSensor = _yocto_ls.YLightSensor
        YRefParam = getattr(_yocto_api, 'YRefParam', None)
        YAPI_SUCCESS = getattr(_yocto_api, 'YAPI_SUCCESS', getattr(YAPI, 'SUCCESS', None))
    except Exception as exc:
        YAPI = None
        YLightSensor = None
        YRefParam = None
        YoctoImportError = exc


class YoctoSensorManager:
    def __init__(self, hub_url='usb', debug=False):
        self.hub_url = hub_url
        self.debug = debug
        self.sensor = None
        self.last_error = ''
        self._api_ready = False

    def _debug(self, message):
        if self.debug:
            print('[Yocto]', message)

    def _make_errmsg(self, msg):
        if YRefParam:
            err = YRefParam()
            try:
                return msg, err
            except Exception:
                return msg, msg
        return msg, msg

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
                return False
            return True
        except Exception as exc:
            self.last_error = f'UpdateDeviceList failed: {exc}'
            return False

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
        if hasattr(self.sensor, 'get_module') and hasattr(self.sensor.get_module(), 'saveToFlash'):
            try:
                self.sensor.get_module().saveToFlash()
            except Exception:
                pass
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
