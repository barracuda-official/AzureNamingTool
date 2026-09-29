using AzureNamingTool.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AzureNamingTool.Attributes
{
    /// <summary>
    /// Authorizes API actions by credential scope and controller/action metadata.
    /// </summary>
    [AttributeUsage(validOn: AttributeTargets.Class)]
    public class ApiKeyAttribute : Attribute, IAsyncActionFilter
    {
        private const string APIKEYNAME = "APIKey";

        private static readonly HashSet<string> ReadOnlyControllers = new(StringComparer.Ordinal)
        {
            "ResourceComponents", "ResourceDelimiters", "ResourceEnvironments",
            "ResourceFunctions", "ResourceLocations", "ResourceOrgs",
            "ResourceProjAppSvcs", "ResourceTypes", "ResourceUnitDepts",
            "CustomComponents"
        };

        private static readonly HashSet<string> GenerationActions = new(StringComparer.Ordinal)
        {
            "RequestName", "RequestNameWithComponents", "ValidateName", "GenerateBulk"
        };

        /// <summary>
        /// Applies the credential scope to the selected MVC action.
        /// </summary>
        /// <param name="context">The action executing context.</param>
        /// <param name="next">The action execution delegate.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (!context.HttpContext.Request.Headers.TryGetValue(APIKEYNAME, out var header)
                || header.Count != 1 || string.IsNullOrWhiteSpace(header[0]))
            {
                context.Result = new StatusCodeResult(401);
                return;
            }

            var config = ConfigurationHelper.GetConfigurationData();
            if (!GeneralHelper.IsNotNull(config) || string.IsNullOrEmpty(config.SALTKey))
            {
                context.Result = new StatusCodeResult(401);
                return;
            }

            var key = header[0]!;
            var salt = config.SALTKey;
            var full = string.Equals(GeneralHelper.DecryptString(config.APIKey!, salt), key, StringComparison.Ordinal);
            var readOnly = string.Equals(GeneralHelper.DecryptString(config.ReadOnlyAPIKey!, salt), key, StringComparison.Ordinal);
            var generation = string.Equals(GeneralHelper.DecryptString(config.NameGenerationAPIKey!, salt), key, StringComparison.Ordinal);
            if (!full && !readOnly && !generation)
            {
                context.Result = new StatusCodeResult(401);
                return;
            }

            var route = context.ActionDescriptor.RouteValues;
            route.TryGetValue("controller", out var controller);
            route.TryGetValue("action", out var action);
            var method = context.HttpContext.Request.Method;
            var allowedRead = method == "GET" && controller != null && ReadOnlyControllers.Contains(controller);
            var allowedGeneration = method == "POST" && controller == "ResourceNamingRequests"
                && action != null && GenerationActions.Contains(action);

            // Import/export and every Admin action require Full Access. The export also
            // contains generated names and admin logs when includeAdmin is false.
            if (!full && !(readOnly && allowedRead) && !(generation && allowedGeneration))
            {
                context.Result = new StatusCodeResult(403);
                return;
            }

            await next();
        }
    }
}
