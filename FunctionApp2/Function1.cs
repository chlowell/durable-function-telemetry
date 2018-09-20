using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using System.IO;
using System.Threading.Tasks;

namespace FunctionApp2
{
    public static class Function1
    {
        private static readonly string key = TelemetryConfiguration.Active.InstrumentationKey =
            "your key here";

        private static readonly StreamWriter file = new StreamWriter(File.Open("operation-ids.txt", FileMode.Create));
        
        [FunctionName("start")]
        public static async Task<IActionResult> Start(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)]HttpRequest req,
            ExecutionContext context,
            [OrchestrationClient] DurableOrchestrationClientBase starter)
        {
            TelemetryConfiguration.Active.TelemetryChannel.DeveloperMode = true;
            var telemetryClient = new TelemetryClient { InstrumentationKey = key };
            telemetryClient.Context.Cloud.RoleName = nameof(Start);

            using (var operation = telemetryClient.StartOperation<DependencyTelemetry>("start orchestrator"))
            {
                file.WriteLine("start");
                file.WriteLine($"invocation id          {context.InvocationId}");


                var instanceId = await starter.StartNewAsync(nameof(Orchestrator), operation.Telemetry.Id);
                file.WriteLine($"orchestration instance {instanceId}");

                operation.Telemetry.Type = "Orchestrator";
                operation.Telemetry.Target = nameof(Orchestrator);
                operation.Telemetry.Success = true;

                return new OkObjectResult(new
                {
                    invocationId = context.InvocationId,
                    operationId = operation.Telemetry.Context.Operation.Id,
                    orchestratorInstance = instanceId
                });
            }
        }

        // these are outside the orchestrator's context to ensure they aren't recreated each replay
        private static IOperationHolder<DependencyTelemetry> dependencyOperation;
        private static IOperationHolder<RequestTelemetry> operation;

        [FunctionName(nameof(Orchestrator))]
        public static async Task Orchestrator([OrchestrationTrigger] DurableOrchestrationContextBase ctx)
        {
            var telemetryClient = new TelemetryClient { InstrumentationKey = key };
            telemetryClient.Context.Cloud.RoleName = nameof(Orchestrator);

            var requestId = ctx.GetInput<string>();

            if (!ctx.IsReplaying) // start operations
            {
                // request telemetry for the orchestrator's execution
                var requestTelemetry = new RequestTelemetry { Name = nameof(Orchestrator) };
                requestTelemetry.Context.Operation.Id = CorrelationHelper.GetOperationId(requestId);
                requestTelemetry.Context.Operation.ParentId = requestId;
                operation = telemetryClient.StartOperation(requestTelemetry);

                file.WriteLine("\norchestrator");
                file.WriteLine($"requestId {requestId}");
                file.WriteLine($"request telemetry id           {operation.Telemetry.Id}");
                file.WriteLine($"request telemetry parent id    {operation.Telemetry.Context.Operation.ParentId}");

                // dependency telemetry for the activity invocation
                var dependencyTelemetry = new DependencyTelemetry
                {
                    Name = $"{nameof(Orchestrator)}_Dependency",
                    Target = "Activity",
                    Type = "activity"
                };
                dependencyTelemetry.Context.Operation.Id = CorrelationHelper.GetOperationId(requestId);
                dependencyTelemetry.Context.Operation.ParentId = operation.Telemetry.Id;
                dependencyOperation = telemetryClient.StartOperation(dependencyTelemetry);

                file.WriteLine($"dependency id        {dependencyOperation.Telemetry.Id}");
                file.WriteLine($"dependency parent id {dependencyOperation.Telemetry.Context.Operation.ParentId}");
            }

            await ctx.CallActivityAsync(nameof(Activity), dependencyOperation.Telemetry.Id);

            if (!ctx.IsReplaying)
            {
                file.WriteLine("\norchestrator");

                // stop dependency telemetry operation
                file.WriteLine($"stopping operation {dependencyOperation.Telemetry.Id}");

                dependencyOperation.Telemetry.Success = true;
                telemetryClient.StopOperation(dependencyOperation);

                // stop request telemetry operation
                file.WriteLine($"stopping operation {operation.Telemetry.Id}");

                operation.Telemetry.Success = true;
                operation.Telemetry.ResponseCode = "200";
                telemetryClient.StopOperation(operation);

                file.Close();
            }
        }

        [FunctionName(nameof(Activity))]
        public static async Task Activity([ActivityTrigger] string requestId)
        {
            file.WriteLine("\nactivity");
            file.WriteLine($"requestId    {requestId}");

            var telemetryClient = new TelemetryClient { InstrumentationKey = key };
            telemetryClient.Context.Cloud.RoleName = nameof(Activity);

            var requestTelemetry = new RequestTelemetry { Name = nameof(Activity) };
            requestTelemetry.Context.Operation.Id = CorrelationHelper.GetOperationId(requestId);
            requestTelemetry.Context.Operation.ParentId = requestId;
            using (var operation = telemetryClient.StartOperation(requestTelemetry))
            {
                file.WriteLine($"telemetry id {operation.Telemetry.Id}");
                file.WriteLine($"telemetry context operation id {operation.Telemetry.Context.Operation.Id}");
                file.WriteLine($"telemetry context parent id    {operation.Telemetry.Context.Operation.ParentId}");

                await Task.Delay(3500);

                operation.Telemetry.Success = true;
            }
        }
    }

    public static class CorrelationHelper
    {
        public static string GetOperationId(string requestId)
        {
            // Returns the root ID from the '|' to the first '.' if any.
            // Following the HTTP Protocol for Correlation - Hierarchical Request-Id schema is used
            // https://github.com/dotnet/corefx/blob/master/src/System.Diagnostics.DiagnosticSource/src/HierarchicalRequestId.md
            int rootEnd = requestId.IndexOf('.');
            if (rootEnd < 0)
                rootEnd = requestId.Length;

            int rootStart = requestId[0] == '|' ? 1 : 0;
            return requestId.Substring(rootStart, rootEnd - rootStart);
        }
    }
}
