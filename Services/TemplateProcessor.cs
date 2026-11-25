namespace Trmnl.Server.Services
{
    using Microsoft.Extensions.Logging;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Reflection;
    using System.Text;
    using System.Text.RegularExpressions;

    public class TemplateProcessor
    {
        // Compile the regex pattern once for better performance
        private static readonly Regex PlaceholderPattern = new Regex(@"\{([^{}:]+)(?::([^{}]+))?\}", RegexOptions.Compiled);
        // Pattern to match function calls: functionName(arg1, arg2, ...)
        private static readonly Regex FunctionPattern = new Regex(@"^(\w+)\((.*)\)$", RegexOptions.Compiled);

        // Maximum recursion depth to prevent stack overflow
        private const int MaxRecursionDepth = 50;

        private readonly HumanizerPluralProvider _plurals;
        private readonly ILogger<TemplateProcessor> _logger;

        public TemplateProcessor(HumanizerPluralProvider plurals, ILogger<TemplateProcessor> logger)
        {
            _logger = logger;
            _plurals = plurals;
        }

        public string EvaluateTemplate(
            string template,
            object? contextObject = null,
            Dictionary<string, Func<string, string>>? knownReplacements = null,
            Dictionary<string, Func<string?, string>>? escapeFunctions = null)
        {
            if (string.IsNullOrEmpty(template))
                return template;

            escapeFunctions ??= new Dictionary<string, Func<string?, string>>();

            // First apply any known replacements
            string result = template;
            if (knownReplacements != null && knownReplacements.Count > 0)
            {
                foreach (var replacement in knownReplacements)
                {
                    string placeholder = replacement.Key;
                    if (result.Contains(placeholder))
                    {
                        try
                        {
                            result = replacement.Value(result);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Error applying known replacement '{Placeholder}'", placeholder);
                        }
                    }
                }
            }

            // Now process any remaining placeholders
            StringBuilder resultBuilder = new StringBuilder(result);

            // Get all matches
            var matches = PlaceholderPattern.Matches(result);

            // Replacement cache to avoid multiple evaluating
            var replacementCache = new Dictionary<string, object?>();

            // Process matches in reverse order to avoid invalidating indices when replacing
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                Match match = matches[i];
                string fullPlaceholder = match.Value; 
                string placeholderPath = match.Groups[1].Value.Trim(); 
                string? format = match.Groups[2].Success ? match.Groups[2].Value.Trim() : null; 
                int startIndex = match.Index;
                int length = match.Length;

                try
                {
                    object? value;

                    if (!replacementCache.TryGetValue(placeholderPath, out var evaluatedObject))
                    {
                        value = EvaluateExpression(placeholderPath, contextObject, 0);
                        replacementCache[placeholderPath] = value;
                    }
                    else
                    {
                        value = evaluatedObject;
                    }

                    if (value != null)
                    {
                        string? replacementValue;

                        // Якщо є формат, застосувати його
                        if (!string.IsNullOrEmpty(format))
                        {
                            if (value is DateTime dateValue)
                            {
                                replacementValue = dateValue.ToString(format);
                            }
                            else if (value is IFormattable formattable)
                            {
                                replacementValue = formattable.ToString(format, CultureInfo.CurrentCulture);
                            }
                            else
                            {
                                replacementValue = value.ToString();
                            }
                        }
                        else
                        {
                            replacementValue = value.ToString();
                        }

                        // Застосувати escape функції
                        replacementValue = ApplyEscapeFunction(placeholderPath, replacementValue, escapeFunctions);

                        if (replacementValue != null)
                        {
                            resultBuilder.Remove(startIndex, length);
                            resultBuilder.Insert(startIndex, replacementValue);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Placeholder '{Placeholder}' couldn't be evaluated, leaving as is", placeholderPath);
                }
            }

             return resultBuilder.ToString();
        }

        // Винесена escape-логіка в окремий метод
        private string? ApplyEscapeFunction(
            string placeholderPath,
            string? value,
            Dictionary<string, Func<string?, string>> escapeFunctions)
        {
            try
            {
                if (escapeFunctions.TryGetValue(placeholderPath, out var escapeFunc))
                {
                    return escapeFunc(value);
                }
                else if (escapeFunctions.TryGetValue("*", out var escapeCommonFunc))
                {
                    return escapeCommonFunc(value);
                }

                return value;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error applying escape function for placeholder '{Placeholder}'", placeholderPath);
                return value; // Return original value on error
            }
        }

        // Helper method to split function arguments, respecting nested functions
        private static List<string> SplitFunctionArguments(string argsString)
        {
            List<string> args = new List<string>();
            if (string.IsNullOrWhiteSpace(argsString))
                return args;

            int depth = 0;
            int lastSplit = 0;

            for (int i = 0; i < argsString.Length; i++)
            {
                char c = argsString[i];

                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    // Split at this position
                    args.Add(argsString.Substring(lastSplit, i - lastSplit));
                    lastSplit = i + 1;
                }
            }

            // Add the last argument
            args.Add(argsString.Substring(lastSplit));

            return args;
        }

        // Execute a function by name with arguments
        private object ExecuteFunction(string functionName, object[] args)
        {
            try
            {
                switch (functionName.ToUpperInvariant())
                {
                    case "IF":
                        // IF(condition, trueValue, falseValue)
                        if (args.Length != 3)
                            throw new ArgumentException("IF function requires 3 arguments: condition, trueValue, falseValue");

                        bool condition = Convert.ToBoolean(args[0]);
                        return condition ? args[1] : args[2];

                    case "CONCAT":
                        // CONCAT(value1, value2, ...)
                        if (args.Length < 1)
                            throw new ArgumentException("CONCAT function requires at least 1 argument");

                        StringBuilder sb = new StringBuilder();
                        foreach (var arg in args)
                        {
                            sb.Append(arg?.ToString() ?? string.Empty);
                        }
                        return sb.ToString();

                    case "FORMAT":
                        // FORMAT(value, format)
                        if (args.Length != 2)
                            throw new ArgumentException("FORMAT function requires 2 arguments: value, format");

                        if (args[0] is DateTime dateValue)
                        {
                            return dateValue.ToString(args[1].ToString());
                        }
                        else if (args[0] is IFormattable formattable)
                        {
                            return formattable.ToString(args[1].ToString(), CultureInfo.CurrentCulture);
                        }
                        return args[0]?.ToString() ?? string.Empty;

                    case "EQUALS":
                        // EQUALS(value1, value2)
                        if (args.Length != 2)
                            throw new ArgumentException("EQUALS function requires 2 arguments: value1, value2");

                        return Equals(args[0], args[1]);

                    case "CONTAINS":
                        // CONTAINS(text, searchText)
                        if (args.Length != 2)
                            throw new ArgumentException("CONTAINS function requires 2 arguments: text, searchText");

                        string text = args[0]?.ToString() ?? string.Empty;
                        string search = args[1]?.ToString() ?? string.Empty;
                        return text.Contains(search);

                    case "UPPER":
                        // UPPER(text)
                        if (args.Length != 1)
                            throw new ArgumentException("UPPER function requires 1 argument: text");

                        return (args[0]?.ToString() ?? string.Empty).ToUpper();

                    case "LOWER":
                        // LOWER(text)
                        if (args.Length != 1)
                            throw new ArgumentException("LOWER function requires 1 argument: text");

                        return (args[0]?.ToString() ?? string.Empty).ToLower();

                    case "PLURAL":
                        // PLURAL(number, key)

                        if (args.Length != 2)
                            throw new ArgumentException(
                                "PLURAL function requires 1 arguments: number, key");

                        int n = Convert.ToInt32(args[0]);
                        string key = args[1]?.ToString() ?? "";

                        return _plurals.Get(key, n);

                    case "PLURALFORM":
                        // PLURALFORM(number, key): the word form only, without the number.
                        if (args.Length != 2)
                            throw new ArgumentException(
                                "PLURALFORM function requires 2 arguments: number, key");

                        return _plurals.GetForm(args[1]?.ToString() ?? "", Convert.ToInt32(args[0]));

                    case "CAPITALIZE":
                        // CAPITALIZE(text): make the first letter upper case.
                        if (args.Length != 1)
                            throw new ArgumentException("CAPITALIZE function requires 1 argument: text");

                        string capText = args[0]?.ToString() ?? string.Empty;
                        return capText.Length == 0
                            ? capText
                            : char.ToUpper(capText[0], CultureInfo.CurrentCulture) + capText[1..];

                    case "SUBSTRING":
                        // SUBSTRING(text, startIndex, length)
                        if (args.Length != 3)
                            throw new ArgumentException("SUBSTRING function requires 3 arguments: text, startIndex, length");

                        string subText = args[0]?.ToString() ?? string.Empty;
                        int startIndex = Convert.ToInt32(args[1]);
                        int length = Convert.ToInt32(args[2]);

                        if (startIndex < 0 || startIndex > subText.Length)
                            return string.Empty;

                        if (startIndex + length > subText.Length)
                            length = subText.Length - startIndex;

                        return subText.Substring(startIndex, length);

                    default:
                        throw new ArgumentException($"Unknown function: {functionName}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error executing function '{FunctionName}'", functionName);
                throw;
            }
        }

        // Separate method to evaluate an expression with recursion depth limit
        private object? EvaluateExpression(string expression, object? contextObject, int depth)
        {
            // String literal: "text"
            if (expression.Length >= 2 &&
                expression.StartsWith("\"") &&
                expression.EndsWith("\""))
            {
                return expression.Substring(1, expression.Length - 2);
            }

            // Check recursion depth
            if (depth > MaxRecursionDepth)
            {
                _logger?.LogError("Maximum recursion depth ({MaxDepth}) exceeded for expression '{Expression}'",
                    MaxRecursionDepth, expression);
                throw new InvalidOperationException($"Maximum recursion depth ({MaxRecursionDepth}) exceeded");
            }

            try
            {
                // Check if the expression is a function call
                Match functionMatch = FunctionPattern.Match(expression);
                if (functionMatch.Success)
                {
                    string functionName = functionMatch.Groups[1].Value;
                    string argsString = functionMatch.Groups[2].Value;

                    List<string> args = SplitFunctionArguments(argsString);

                    // Evaluate each argument recursively
                    var evaluatedArgs = new List<object?>();
                    foreach (var arg in args)
                    {
                        evaluatedArgs.Add(EvaluateExpression(arg.Trim(), contextObject, depth + 1));
                    }

                    // Execute the function
                    return ExecuteFunction(functionName, evaluatedArgs.ToArray());
                }

                // Check for special expressions first
                if (TryEvaluateSpecialExpression(expression, out object? specialValue))
                {
                    return specialValue;
                }

                // Split the path into parts (e.g., "user.profile.name" -> ["user", "profile", "name"])
                string[] pathParts = expression.Split('.');

                if (pathParts.Length == 0)
                    return null;

                // Start with the first object in the path
                string rootName = pathParts[0];
                object? currentObj = null;
                bool foundRoot = false;

                // First try the provided context object
                if (contextObject != null)
                {
                    // If context is a dictionary
                    if (contextObject is IDictionary<string, object> contextDict)
                    {
                        if (contextDict.TryGetValue(rootName, out currentObj))
                        {
                            foundRoot = true;
                        }
                    }
                    // If context is a regular object, check its properties
                    else
                    {
                        var contextProperty = contextObject.GetType().GetProperty(rootName,
                            BindingFlags.Public | BindingFlags.Instance);

                        if (contextProperty != null)
                        {
                            currentObj = contextProperty.GetValue(contextObject);
                            foundRoot = true;
                        }
                    }
                }

                // If not found in current instance, try resolving as a static class
                if (!foundRoot)
                {
                    Type? staticType = GetTypeByName(rootName);
                    if (staticType != null)
                    {
                        foundRoot = true;

                        if (pathParts.Length > 1)
                        {
                            // Get the static property
                            var staticProperty = staticType.GetProperty(pathParts[1],
                                BindingFlags.Public | BindingFlags.Static);

                            if (staticProperty != null)
                            {
                                currentObj = staticProperty.GetValue(null);

                                // Start navigation from the 3rd element (index 2)
                                for (int i = 2; i < pathParts.Length && currentObj != null; i++)
                                {
                                    currentObj = NavigateProperty(currentObj, pathParts[i]);
                                    if (currentObj == null)
                                        break;
                                }

                                return currentObj;
                            }
                        }
                    }
                }

                // Navigate through the rest of the path for non-static cases
                if (foundRoot && currentObj != null)
                {
                    // Navigate through the rest of the path
                    for (int i = 1; i < pathParts.Length && currentObj != null; i++)
                    {
                        currentObj = NavigateProperty(currentObj, pathParts[i]);
                        if (currentObj == null)
                            break;
                    }
                }

                return currentObj;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error evaluating expression '{Expression}' at depth {Depth}", expression, depth);
                throw;
            }
        }

        // Helper method to navigate to a property on an object
        private object? NavigateProperty(object obj, string propertyName)
        {
            try
            {
                // Check if it's a dictionary access
                if (obj is IDictionary<string, object> dict)
                {
                    if (dict.TryGetValue(propertyName, out object? dictValue))
                    {
                        return dictValue;
                    }
                }

                // Regular property access
                PropertyInfo? property = obj.GetType().GetProperty(propertyName,
                    BindingFlags.Public | BindingFlags.Instance);

                if (property != null)
                {
                    return property.GetValue(obj);
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error navigating to property '{PropertyName}' on object of type '{ObjectType}'",
                    propertyName, obj.GetType().Name);
                return null;
            }
        }

        // Helper method to evaluate special expressions like DateTime.Now
        private static bool TryEvaluateSpecialExpression(string expression, out object? value)
        {
            value = null;

            // Handle DateTime.Now
            if (expression.Equals("DateTime.Now", StringComparison.OrdinalIgnoreCase))
            {
                value = DateTime.Now;
                return true;
            }

            // Handle DateTime.Today
            if (expression.Equals("DateTime.Today", StringComparison.OrdinalIgnoreCase))
            {
                value = DateTime.Today;
                return true;
            }

            // Handle DateTime.UtcNow
            if (expression.Equals("DateTime.UtcNow", StringComparison.OrdinalIgnoreCase))
            {
                value = DateTime.UtcNow;
                return true;
            }

            return false;
        }

        // Helper method to get a Type by its name
        private Type? GetTypeByName(string typeName)
        {
            try
            {
                // Try common namespaces
                string[] commonNamespaces = new[]
                {
                "", // Current namespace
                "System",
                "System.Collections.Generic",
                "System.Linq",
                "System.IO",
                "System.Text"
            };

                foreach (var ns in commonNamespaces)
                {
                    string fullName = string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";
                    Type? type = Type.GetType(fullName);
                    if (type != null)
                        return type;

                    // Try with assembly qualified name for system types
                    if (ns == "System")
                    {
                        type = Type.GetType($"{fullName}, mscorlib");
                        if (type != null)
                            return type;
                    }
                }

                // Search loaded assemblies
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type? type = assembly.GetType(typeName);
                    if (type != null)
                        return type;

                    type = assembly.GetType($"System.{typeName}");
                    if (type != null)
                        return type;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error resolving type '{TypeName}'", typeName);
                return null;
            }
        }
    }
}
