using System;

namespace AutoBright.Hardware;

/// <summary>
/// Wraps the Yoctopuce ambient light sensor: hub registration, sensor discovery,
/// lux readings, reconnect handling, and (best-effort) suppression of the sensor
/// board's status LEDs. A direct port of the original Python sensor manager.
///
/// The Yoctopuce .NET classes (YAPI, YLightSensor, YModule, YLed, YColorLed) live
/// in the global namespace, so no using directive is required for them.
/// </summary>
public sealed class YoctoSensorManager
{
    public string HubUrl { get; }
    public bool Debug { get; }
    public string LastError { get; private set; } = "";

    private YLightSensor? _sensor;
    private bool _apiReady;
    private bool _ledsConfigured;

    public YoctoSensorManager(string hubUrl = "usb", bool debug = false)
    {
        HubUrl = string.IsNullOrWhiteSpace(hubUrl) ? "usb" : hubUrl;
        Debug = debug;
    }

    /// <summary>Force a full reconnect on the next tick.</summary>
    public void Reset()
    {
        _sensor = null;
        _apiReady = false;
        _ledsConfigured = false;
    }

    /// <summary>Service the connection: register the hub or refresh the device list.</summary>
    public void Tick()
    {
        if (!_apiReady)
        {
            RegisterHub();
        }
        else
        {
            UpdateDeviceList();
        }
    }

    public double? GetLux()
    {
        if (_sensor == null && !FindSensor())
        {
            return null;
        }
        try
        {
            if (_sensor!.isOnline() == false)
            {
                _sensor = null;
                return null;
            }
            return _sensor.get_currentValue();
        }
        catch (Exception exc)
        {
            LastError = $"Unable to read sensor value: {exc.Message}";
            _sensor = null;
            return null;
        }
    }

    private bool RegisterHub()
    {
        LastError = "";
        try
        {
            string err = "";
            int result = YAPI.RegisterHub(HubUrl, ref err);
            if (result != YAPI.SUCCESS)
            {
                LastError = err;
                return false;
            }
            _apiReady = true;
            return true;
        }
        catch (Exception exc)
        {
            LastError = $"RegisterHub failed: {exc.Message}";
            return false;
        }
    }

    private bool UpdateDeviceList()
    {
        if (!_apiReady)
        {
            return false;
        }
        try
        {
            string err = "";
            int result = YAPI.UpdateDeviceList(ref err);
            if (result != YAPI.SUCCESS)
            {
                LastError = err;
                // The hub was likely lost; force re-registration next tick.
                _apiReady = false;
                return false;
            }
            return true;
        }
        catch (Exception exc)
        {
            LastError = $"UpdateDeviceList failed: {exc.Message}";
            _apiReady = false;
            return false;
        }
    }

    private bool FindSensor()
    {
        if (!_apiReady && !RegisterHub())
        {
            return false;
        }

        try
        {
            _sensor = YLightSensor.FirstLightSensor();
        }
        catch (Exception exc)
        {
            _sensor = null;
            LastError = $"Unable to enumerate Yocto sensors: {exc.Message}";
            return false;
        }

        if (_sensor == null || !_sensor.isOnline())
        {
            _sensor = null;
            LastError = "No Yocto light sensor found.";
            return false;
        }

        try { _sensor.set_reportFrequency("60/m"); } catch { /* best effort */ }

        // LED/beacon state is persisted to flash, which has limited write
        // endurance, so configure it at most once per connection.
        if (!_ledsConfigured)
        {
            try
            {
                YModule module = _sensor.get_module();
                if (module != null)
                {
                    try { module.set_luminosity(0); } catch { }
                    try { module.set_beacon(YModule.BEACON_OFF); } catch { }
                    try { module.saveToFlash(); } catch { }
                }
            }
            catch { /* best effort */ }
            DisableYoctoLeds();
            _ledsConfigured = true;
        }

        LastError = "";
        return true;
    }

    /// <summary>
    /// Best-effort only, per the class doc comment: every device call is
    /// individually wrapped so one failing LED/enumeration step can't stop the
    /// rest, and a failure here must never surface as a worker-cycle error.
    /// </summary>
    private static void DisableYoctoLeds()
    {
        try
        {
            YLed? led = YLed.FirstLed();
            while (led != null)
            {
                try { led.set_power(YLed.POWER_OFF); } catch { }
                try { led = led.nextLed(); } catch { break; }
            }
        }
        catch { }

        try
        {
            YColorLed? colorLed = YColorLed.FirstColorLed();
            while (colorLed != null)
            {
                try { colorLed.set_rgbColor(0); } catch { }
                try { colorLed = colorLed.nextColorLed(); } catch { break; }
            }
        }
        catch { }
    }
}
