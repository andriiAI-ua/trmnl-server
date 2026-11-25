# trmnl-server

A self-hosted server (BYOS) for the [TRMNL](https://usetrmnl.com) e-paper display, written in ASP.NET Core (.NET 10).
It draws the screens from Home Assistant data and from Ukrainian public sources (meteo.gov.ua forecasts,
air-raid alerts, power-outage schedules). The screen texts are in Ukrainian.

The server also gives a 400x300 screen to the RLCD device of the
[rlcd-dashboard](https://github.com/andriiAI-ua/rlcd-dashboard) project.

## Features

- **TRMNL API**: `/api/setup`, `/api/display`, `/api/log`. The image is an 800x480 PNG, 1-bit or 4 grays.
- **Screens**, the same as on the RLCD device:
  - «Дашборд»: date, outdoor temperature and humidity with the change in 24 hours, tomorrow's forecast,
    air-raid alert, power (grid, generator, UPS) and the next outage, the year progress;
  - «4 квадрати» and «5 квадратів»: date, outdoor air, alert, power, or the forecast for 3 days;
  - «10 днів»: the city forecast for 10 days;
  - «Текст»: a text that you type in Home Assistant;
  - «Кімната»: the room climate;
  - «Годинник»: an analog clock. The device wakes just after each N-minute step;
  - «Дні року»: the days of the year.
- **Home Assistant control over MQTT discovery.** Each device gets a select «Екран», a number «Оновлення екрана»
  (1–240 minutes), a text «Текст», and sensors: firmware version, battery, Wi-Fi signal, last request.
- **The button** (with the [own firmware](https://github.com/andriiAI-ua/trmnl-firmware)): a short press shows the next screen.
- **Full refresh after a change of the screen** (`maximum_compatibility`), so that the old screen leaves no ghost.
- **Forecasts in the background.** The server reads meteo.gov.ua in the background (the informer each hour,
  the 10-day city forecast after 06:00 and 18:00) and keeps the last forecasts in `Data/`.
  A screen never waits for the site.
- **Firmware updates (OTA)** from own builds: put pairs of `*.bin` and `*.json` (from `build.sh` of the
  [trmnl-firmware fork](https://github.com/andriiAI-ua/trmnl-firmware)) into `wwwroot/firmware`. A device gets only a newer version; the server counts the downloads
  and stops an update loop.
- `/quad.bmp`: the 400x300 1-bit BMP for the RLCD device. `/screen.png`: the current screen for a web page.
- `/api/power/status`: a webhook for the real power status of the building (`{"status": "up"}` or `"down"`).

## Configuration

`appsettings.json` has only empty values. Put the values of your installation into
`Data/appsettings.local.json`: the server reads it after `appsettings.json`, and a new release keeps it.

```json
{
  "HomeAssistant": {
    "Url": "http://homeassistant.local:8123",
    "Token": "<long-lived access token>"
  },
  "HaMqtt": {
    "Broker": "homeassistant.local",
    "Port": 1883,
    "Username": "<mqtt user>",
    "Password": "<mqtt password>"
  },
  "Dashboard": {
    "PowerQueue": "черга 1.1",
    "Temperature": [ "sensor.outdoor_temperature" ],
    "Humidity": [ "sensor.outdoor_humidity" ],
    "AirQuality": [ "sensor.outdoor_air_quality_index" ],
    "Voltage": [ "sensor.ups_input_voltage" ],
    "UpsStatus": "sensor.ups_status",
    "DroneAlert": "binary_sensor.air_alert_drones",
    "MissileAlert": "binary_sensor.air_alert_missiles",
    "NextPowerOff": "sensor.next_power_off",
    "NextPowerOn": "sensor.next_power_on",
    "ForecastStation": "<meteo.gov.ua informer station>",
    "ForecastCity": "<city>",
    "ForecastRegion": "<region>",
    "RoomTemperature": [ "sensor.room_temperature" ],
    "RoomHumidity": [ "sensor.room_humidity" ]
  }
}
```

- A list of entities gives the first entity with a value.
- An empty `HaMqtt:Broker` turns the Home Assistant control off.
- Without `ForecastCity` there are no «5 квадратів» and «10 днів» screens; without `RoomTemperature`, no «Кімната».
- `sudo ./configure-ha.sh` writes the Home Assistant URL and token into this file and keeps the other settings.

## Build and deploy

On a Linux server with the .NET 10 SDK, in the folder of this repository:

```bash
git pull && ./build.sh && sudo ./deploy.sh
```

- `build.sh` makes a self-contained package `build-v<version>-<rid>.tar.gz`. The version continues from the highest
  of `VERSION`, the packages and the installed releases.
- `deploy.sh` installs the release into `/opt/trmnl-server/v<version>`, switches the link `current`, restarts
  `trmnl-server.service` (port 15000) and checks `/health`. If the check fails, it goes back to the previous
  release. It keeps 3 releases. The shared data is in `/opt/trmnl-server/data`.
- `rollback.sh`: go back to the previous release.
- `beta.sh` and `promote.sh`: a beta instance on port 15001, and its switch to production.
- `publish-screen.sh SITE`: an nginx location that publishes only `/trmnl/screen.png`.

On the device, open the Wi-Fi setup page and set **API server** to `http://<server>:15000`.

## Development

```bash
dotnet run
dotnet test tests/trmnl-server.Tests/trmnl-server.Tests.csproj
```

In the Development environment, `/dev/preview?screen=clock&sample=calm&time=10:10` shows a screen with sample data
(`screen`: quad, quad5, days10, note, room, clock; `sample`: calm, drone, missile, off, generator, …).

## License

MIT, see [LICENSE](LICENSE).
