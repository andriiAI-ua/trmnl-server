namespace Trmnl.Server.Services
{
    using System;
    using System.IO;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Provides access to Markdown resources from either the file system or embedded resources.
    /// File system resources (in AppBase/Markdown folder) take precedence over embedded resources.
    /// </summary>
    public class JsonResourceProvider
    {
        private readonly string _baseDirectory;
        private readonly Assembly _assembly;
        private readonly string _embeddedResourceNamespace;

        /// <summary>
        /// Initializes a new instance of the MarkdownResourceProvider.
        /// </summary>
        /// <param name="embeddedResourceNamespace">The namespace prefix for embedded resources (e.g., "YourNamespace.Markdown")</param>
        public JsonResourceProvider(string? embeddedResourceNamespace = null)
        {
            _baseDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");
            _assembly = Assembly.GetExecutingAssembly();
            _embeddedResourceNamespace = embeddedResourceNamespace ?? $"{_assembly.GetName().Name}.Resources";
        }

        /// <summary>
        /// Gets the content of a Json resource by name asynchronously.
        /// Checks file system first, then falls back to embedded resources.
        /// </summary>
        /// <param name="resourceName">The name of the resource (without .md extension)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The content of the Json file</returns>
        /// <exception cref="InvalidOperationException">Thrown when the resource is not found in either location</exception>
        public async Task<string> GetJsonAsync(string resourceName, CancellationToken cancellationToken = default)
        {
            // Ensure .md extension
            if (!resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                resourceName += ".json";
            }

            // Try to read from file system first
            string filePath = Path.Combine(_baseDirectory, resourceName);
            if (File.Exists(filePath))
            {
                return await File.ReadAllTextAsync(filePath, System.Text.Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            }

            // Fall back to embedded resource
            string embeddedResourceName = $"{_embeddedResourceNamespace}.{resourceName}";
            using (var stream = _assembly.GetManifestResourceStream(embeddedResourceName))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException(
                        $"Markdown resource '{resourceName}' not found in file system ('{filePath}') or embedded resources ('{embeddedResourceName}')");
                }

                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Tries to get the content of a json resource by name asynchronously.
        /// </summary>
        /// <param name="resourceName">The name of the resource (without .md extension)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The content of the json file if found; otherwise, null</returns>
        public async Task<string?> TryGetJsonAsync(string resourceName, CancellationToken cancellationToken = default)
        {
            try
            {
                return await GetJsonAsync(resourceName, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>
        /// Checks if a json resource exists in either file system or embedded resources.
        /// </summary>
        /// <param name="resourceName">The name of the resource (without .json extension)</param>
        /// <returns>True if the resource exists; otherwise, false</returns>
        public bool ResourceExists(string resourceName)
        {
            if (!resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                resourceName += ".json";
            }

            string filePath = Path.Combine(_baseDirectory, resourceName);
            if (File.Exists(filePath))
            {
                return true;
            }

            string embeddedResourceName = $"{_embeddedResourceNamespace}.{resourceName}";
            using var stream = _assembly.GetManifestResourceStream(embeddedResourceName);
            return stream != null;
        }
    }
}
