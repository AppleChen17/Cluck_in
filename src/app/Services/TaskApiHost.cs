using Microsoft.Extensions.Configuration;
using System.Windows.Threading;
using CluckIn.App.Interfaces;
using CluckIn.App.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CluckIn.App.Services;

public static class TaskApiHost
{
    public static WebApplication Create(Dispatcher dispatcher, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.UseUrls("http://127.0.0.1:5180");
        // Desktop users need no Windows Event Log write permission.
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole();
        builder.Logging.AddDebug();
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        builder.Configuration.AddJsonFile(System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddEnvironmentVariables();
        builder.Services.Configure<AiEngineOptions>(builder.Configuration.GetSection("AiEngine"));
        builder.Services.Configure<ExternalMessagesOptions>(builder.Configuration.GetSection("ExternalMessages"));
        DesktopAgentFactory.RegisterServices(builder.Services);
        configureServices?.Invoke(builder.Services);
        var app = builder.Build();

        // A web page must not be able to launch local programs through a blind form POST.
        // Vite proxies same-origin JSON requests; no cross-origin access is enabled.
        app.Use(async (context, next) =>
        {
            if (context.Request.Host.Host is not ("127.0.0.1" or "localhost"))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { success = false, error = "Host is not allowed." });
                return;
            }
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && origin is not ("http://localhost:5173" or "http://127.0.0.1:5173" or "http://localhost:5180" or "http://127.0.0.1:5180"))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { success = false, error = "Origin is not allowed." });
                return;
            }
            if ((HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method)) && !context.Request.HasJsonContentType())
            {
                context.Response.StatusCode = 415;
                await context.Response.WriteAsJsonAsync(new { success = false, error = "Content-Type must be application/json." });
                return;
            }
            try { await next(context); }
            catch (Exception exception)
            {
                app.Logger.LogError(exception, "Task API request failed");
                context.Response.StatusCode = exception switch
                {
                    KeyNotFoundException => 404,
                    InvalidOperationException => 409,
                    ArgumentException or BadHttpRequestException => 400,
                    _ => 500
                };
                await context.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    error = context.Response.StatusCode == 500 ? "Task operation failed. See Desktop Agent logs." : exception.Message
                });
            }
        });

        app.MapPost("/input-event", async (MainInputEvent input, DesktopAgentService agent) =>
            await (await dispatcher.InvokeAsync(async () =>
            {
                await agent.HandleInputEventAsync(input);
                return Results.Ok(new { success = true });
            })));
        app.MapPost("/api/main/pat", async (DesktopAgentService agent) =>
        {
            await agent.HandlePatAsync();
            return Results.Ok(new { success = true });
        });

        app.MapGet("/api/tasks", (ITaskRepository repository) => repository.GetTasksAsync());
        app.MapGet("/api/tasks/{taskId}", async (string taskId, ITaskManager manager) =>
            await manager.GetTaskAsync(taskId) is { } profile
                ? Results.Ok(profile)
                : Results.NotFound(new { success = false, error = $"Task not found: {taskId}" }));
        app.MapPut("/api/tasks/{taskId}", async (string taskId, TaskProfile profile, ITaskRepository repository) =>
        {
            if (taskId != profile.Id) throw new ArgumentException("Task ID must match the URL.");
            await repository.SaveTaskAsync(profile);
            return Results.Ok(profile);
        });
        app.MapPost("/api/tasks/{taskId}/start", async (string taskId, ITaskManager manager, ISessionManager session) =>
        {
            // Timer/workspace/UI all run on the WPF dispatcher, with the same DI instances.
            return await (await dispatcher.InvokeAsync(async () =>
            {
                await manager.StartTaskAsync(taskId);
                return Results.Ok(new { success = true, taskId = session.CurrentTask!.Id, taskName = session.CurrentTask.Name });
            }));
        });
        app.MapPost("/api/session/end-task", async (DesktopAgentService agent) =>
            await dispatcher.InvokeAsync(() =>
            {
                agent.EndTask();
                return Results.Ok(new { success = true });
            }));
        app.MapGet("/api/session", async (ISessionManager session, ITimerManager timer) =>
            await dispatcher.InvokeAsync(() => Results.Ok(new { currentTask = session.CurrentTask, focusSession = timer.GetCurrentSession() })));
        app.MapGet("/api/intervention", async (DesktopAgentService agent) =>
            await dispatcher.InvokeAsync(() => Results.Ok(agent.Intervention)));
        app.MapPost("/api/intervention/action", async (InterventionActionRequest request, DesktopAgentService agent) =>
            await (await dispatcher.InvokeAsync(async () => Results.Ok(await agent.HandleInterventionAsync(request)))));
        return app;
    }
}
