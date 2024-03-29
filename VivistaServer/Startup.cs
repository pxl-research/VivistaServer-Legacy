﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using tusdotnet;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;
using tusdotnet.Stores;
using static VivistaServer.CommonController;

namespace VivistaServer
{
	public class Startup
	{
		private static Router router;

		public void ConfigureServices(IServiceCollection services)
		{
			services.Configure<FormOptions>(config => { config.MultipartBodyLengthLimit = long.MaxValue; });

			router = new Router();

			EmailClient.InitCredentials();

			HTMLRenderer.RegisterLayout(BaseLayout.Web, "Templates/base.liquid");

			CheckForFfmpeg();

			CreateDataDirectoryIfNeeded();

			Database.PerformMigrations();

			DashboardController.RegisterRestart();
		}

		public void CheckForFfmpeg()
		{
			if (String.IsNullOrEmpty(Path.GetFullPath("ffmpeg")))
			{
				Console.WriteLine("ffmpeg executable not found in PATH");
				Environment.Exit(-1);
			}
		}

		public void CreateDataDirectoryIfNeeded()
		{
			Directory.CreateDirectory(VideoController.baseFilePath);
		}

		public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
		{
			if (env.IsDevelopment())
			{
				app.UseDeveloperExceptionPage();
				CommonController.baseURL = "https://localhost:5001";
			}
			else
			{
				app.UseExceptionHandler(exceptionHandlerApp =>
				{
					exceptionHandlerApp.Run(async context =>
					{
						var exceptionHandler = context.Features.Get<IExceptionHandlerPathFeature>();
						//NOTE(Simon): This will only happen in the live version, so that we can keep Developer Exception Pages working locally
						DashboardController.AddUnCaughtException();
						await CommonController.WriteError(context, "The server encountered an unexpected error", StatusCodes.Status500InternalServerError, exceptionHandler?.Error);
					});
				});

				app.UseHsts();
				CommonController.baseURL = "https://vivista.net";
			}


			CommonController.wwwroot = env.WebRootPath;
			app.UseStaticFiles();
			app.UseTus(context =>
			{
				if (!context.Request.Path.StartsWithSegments(new PathString("/api/file")) || context.Request.Method == "GET")
				{
					return null;
				}

				//NOTE(Tom): Is for initialization
				context.Items[DashboardController.RENDER_TIME] = 0f;
				context.Items[DashboardController.DB_EXEC_TIME] = 0f;

				var guid = new Guid(context.Request.Headers["guid"]);
				string path = Path.Combine(VideoController.baseFilePath, guid.ToString());
				return new DefaultTusConfiguration
				{
					Store = new TusDiskStore(path),
					UrlPath = "/api/file",
					Events = new Events
					{
						OnAuthorizeAsync = VideoController.AuthorizeUploadTus,
						OnBeforeCreateAsync = async createContext => Directory.CreateDirectory(path),
						//NOTE(Simon): Do not allow deleting by someone trying to exploit the protocol
						OnBeforeDeleteAsync = async deleteContext => deleteContext.FailRequest(""),
						OnFileCompleteAsync = VideoController.ProcessUploadTus,
					}
				};
			});

			Task.Run(CollectPeriodicStatistics);
			Task.Run(RoleController.LoadRoles);

			app.Run(async (context) =>
			{
				//NOTE(Tom): Is for initialization
				context.Items[DashboardController.RENDER_TIME] = 0f;
				context.Items[DashboardController.DB_EXEC_TIME] = 0f;

				var requestTime = Stopwatch.StartNew();

				PrintDebugData(context);

				SetJSONContentType(context);

				CommonController.LogDebug($"request preamble: {requestTime.Elapsed.TotalMilliseconds} ms");

				await router.RouteAsync(context.Request, context);
				requestTime.Stop();
				IFormCollection form = null;

				if (router.RouteExists(context.Request))
				{
					//NOTE(Tom): Do no not allow to show password in database
					if (context.Request.HasFormContentType && !context.Request.Form.ContainsKey("password"))
					{
						form = context.Request.Form;
					}

					var requestInfo = new RequestInfo
					{
						query = (QueryCollection)context.Request.Query,
						form = (FormCollection)form
					};

					var request = new Request
					{
						seconds = (float)requestTime.Elapsed.TotalSeconds,
						requestInfo = requestInfo,
						endpoint = $"/{context.Request.Method}:  {context.Request.Path.Value}",
						renderTime = (float)context.Items[DashboardController.RENDER_TIME],
						dbExecTime = (float)context.Items[DashboardController.DB_EXEC_TIME]

					};
					DashboardController.AddRequestToCache(request);
				}
			});
		}

		private void PrintDebugData(HttpContext context)
		{
#if DEBUG
			var watch = Stopwatch.StartNew();
			CommonController.LogDebug("Request data:");
			CommonController.LogDebug($"\tPath: {context.Request.Path}");
			CommonController.LogDebug($"\tMethod: {context.Request.Method}");
			CommonController.LogDebug("\tQuery: ");
			foreach (var kvp in context.Request.Query)
			{
				CommonController.LogDebug($"\t\t{kvp.Key}: {kvp.Value}");
			}
			CommonController.LogDebug("\tHeaders: ");
			foreach (var kvp in context.Request.Headers)
			{
				CommonController.LogDebug($"\t\t{kvp.Key}: {kvp.Value}");
			}
			if (!context.Request.HasFormContentType)
			{
				CommonController.LogDebug($"\tBody: {new StreamReader(context.Request.Body).ReadToEnd()}");
			}
			watch.Stop();
			CommonController.LogDebug($"writing debug info: {watch.Elapsed.TotalMilliseconds} ms");
#endif
		}

		//TODO(Simon): Minute data should probably work similarly to hour/day, so we don't have to rely on the margin after rounding.
		private static async Task CollectPeriodicStatistics()
		{
			var lastHours = DateTime.UtcNow;
			var lastDay = DateTime.UtcNow;
			while (true)
			{
				//NOTE(Simon): Add a little margin to account for rounding errors in Task.Delay. If ran without margin, Task.Delay would sometimes be done too early causing many rapid runs of the AddMinuteData Task
				var nextTime = DateTime.UtcNow.RoundUp(TimeSpan.FromSeconds(60)) + TimeSpan.FromSeconds(1);

				var delay = nextTime - DateTime.UtcNow;
				await Task.Delay(delay);

				Task minute, hour, day;
				minute = Task.Run(DashboardController.AddMinuteData);
				hour = day = Task.CompletedTask;
				if (DateTime.UtcNow.Hour != lastHours.Hour)
				{
					var hoursTemp = lastHours;
					hour = Task.Run(() => DashboardController.AddHourData(hoursTemp));
					lastHours = DateTime.UtcNow;
				}

				if (DateTime.UtcNow.Day != lastDay.Day)
				{
					var dayTemp = lastDay;
					day = Task.Run(() => DashboardController.AddDayData(dayTemp));
					lastDay = DateTime.UtcNow;
				}

				Task.WaitAll(minute, hour, day);
			}
		}

		public static List<string> GetEndpointsOfRoute()
		{
			return router.GetDatabaseFormattedEndpoints();
		}
	}
}
