namespace Trmnl.Server.Services
{
    using Humanizer;
    using System.Globalization;
    using System.Text.Json;

    public record PluralForms
    {
        public string One { get; set; }  // 1, 21, 101...
        public string Few { get; set; }  // 2-4, 22-24...
        public string Many { get; set; } // 5-0, 11-14...
    }

    public sealed class HumanizerPluralProvider
    {
        private readonly Dictionary<string, PluralForms> _baseForms;
        private readonly CultureInfo culture;

        public HumanizerPluralProvider(JsonResourceProvider jsonProvider)
        {
            culture = CultureInfo.GetCultureInfo(CultureInfo.DefaultThreadCurrentCulture.Name);

            var json = "";
            //todo
            if (jsonProvider.ResourceExists($"plurals.{CultureInfo.DefaultThreadCurrentCulture}.json"))
            {
                json = jsonProvider.GetJsonAsync($"plurals.{CultureInfo.DefaultThreadCurrentCulture}.json").Result;
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            _baseForms = JsonSerializer.Deserialize
                <Dictionary<string, PluralForms>>(json, options)
                ?? throw new InvalidOperationException(
                    "Plural dictionary is empty");
        }

        public string Get(string key, int number)
        {
            return $"{number} {GetForm(key, number)}";
        }

        // Return only the word form for the number, for example the verb form: "минув", "минули", "минуло".
        public string GetForm(string key, int number)
        {
            if (!_baseForms.TryGetValue(key, out var baseForm))
            {
                throw new KeyNotFoundException(
                    $"Plural key '{key}' not found");
            }

            return GetPlural(number, baseForm);
        }

        public static string GetPlural(int n, PluralForms forms)
        {
            // 10111 % 100 == 11 -> many
            int mod10 = n % 10;
            int mod100 = n % 100;

            if (mod100 >= 11 && mod100 <= 14)
                return forms.Many;

            if (mod10 == 1)
                return forms.One;

            if (mod10 >= 2 && mod10 <= 4)
                return forms.Few;

            return forms.Many;
        }
    }
}