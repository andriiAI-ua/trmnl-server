using Microsoft.AspNetCore.Mvc;

namespace Trmnl.Server.Models
{
    public sealed class TrmnlRequestHeaders
    {
        [FromHeader(Name = "ID")]
        public string DeviceId { get; init; } = default!;

        [FromHeader(Name = "Access-Token")]
        public string AccessToken { get; init; } = default!;

        [FromHeader(Name = "FW-Version")]
        public string? FirmwareVersion { get; init; }

        [FromHeader(Name = "Battery-Voltage")]
        public decimal? BatteryVoltage { get; init; }

        [FromHeader(Name = "RSSI")]
        public int? Rssi { get; init; }

        // Screen size of the device. A small screen gets the compact dashboard.
        [FromHeader(Name = "Width")]
        public int? Width { get; init; }

        [FromHeader(Name = "Height")]
        public int? Height { get; init; }

        // Own firmware: "next" after a short press of the button. The device then asks for the next screen.
        [FromHeader(Name = "Button")]
        public string? Button { get; init; }
    }
}
