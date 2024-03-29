﻿using Fluid;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Diagnostics;
using static VivistaServer.CommonController;
using System.Collections.Generic;
using System;
using Dapper;
using Npgsql;
using System.Linq;

namespace VivistaServer
{
	public class RoleController
	{
		private static List<Role> allRoles;

		[Route("GET", "/admin/role")]
		private static async Task OpenRolesPage(HttpContext context)
		{
			using var connection = Database.OpenNewConnection();
			if (await User.IsUserAdmin(context, connection))
			{
				await LoadRolesPage(context);
			}
			else
			{
				await CommonController.Write404(context);
			}
		}

		[Route("POST", "/admin/role")]
		private static async Task AddAdmin(HttpContext context)
		{
			using var connection = Database.OpenNewConnection();
			if (await User.IsUserAdmin(context, connection))
			{
				int roleid = GetRoleId("admin");

				var form = context.Request.Form;
				string username = form["username"];

				int userid = await Database.QuerySingleOrDefaultAsync<int>(connection, @"SELECT userId 
																							FROM users
																							WHERE username = @username;", context, new { username });
				if (userid > 0)
				{
					if (!await UserRoleExist(userid, roleid, connection, context))
					{
						await Database.ExecuteAsync(connection, "INSERT INTO user_roles(userid, roleid) VALUES(@userid, @roleid)", context, new { userid, roleid });
						await LoadRolesPage(context, "", $"{username} is a new admin!");
					}
					else
					{
						await LoadRolesPage(context, $"{username} is already admin.");
					}
				}
				else
				{
					await LoadRolesPage(context, "Username doesn't exist");
				}
			}
			else
			{
				await CommonController.Write404(context);
			}
		}

		[Route("GET", "/admin/deleteAdmin")]
		private static async Task DeleteAdmin(HttpContext context)
		{
			using var connection = Database.OpenNewConnection();
			if (await User.IsUserAdmin(context, connection))
			{
				var username = context.Request.Query["username"].ToString();

				var userid = await Database.QuerySingleOrDefaultAsync<int>(connection, @"SELECT userid 
																							FROM users 
																							WHERE username = @username;", context, new { username });

				var roleid = GetRoleId("admin");
				var loggedInUser = await UserSessions.GetLoggedInUser(context);
				if (userid > 0)
				{
					if (loggedInUser.userid != userid)
					{
						try
						{
							await Database.ExecuteAsync(connection, @"DELETE FROM user_roles 
																		WHERE userid = @userid AND roleid = @roleid;", context, new { userid, roleid });
							await LoadRolesPage(context, "", $"{username} his admin role is deleted.");
						}
						catch (Exception ex)
						{
							await LoadRolesPage(context, "Something went wrong");
						}
					}
					else
					{
						await LoadRolesPage(context, "You can't delete your own admin role.");
					}
				}
				else
				{
					await LoadRolesPage(context, "User doesn't exist");
				}
			}
			else
			{
				await CommonController.Write404(context);
			}
		}

		public static async Task LoadRolesPage(HttpContext context, string error = "", string success = "")
		{
			int roleid = GetRoleId("admin");

			SetHTMLContentType(context);
			await using var connection = Database.OpenNewConnection();

			var admins = await Database.QueryAsync<string>(connection,
				@"SELECT users.username as username 
					FROM users 
					INNER JOIN user_roles ON users.userid = user_roles.userid 
					WHERE roleid = @roleid;", context, new { roleid });

			var templateContext = new TemplateContext(new
			{
				admins,
				error,
				success
			});

			await context.Response.WriteAsync(await HTMLRenderer.Render(context, "Templates\\roles.liquid", templateContext));
		}

		public static int GetRoleId(string role)
		{
			Debug.Assert(role.ToLower() == role, "Role needs to be written in lowercase");

			int roleid = 0;
			try
			{
				foreach (var r in allRoles)
				{
					if (r.name == role)
					{
						roleid = r.id;
						break;
					}
				}
			}
			catch (Exception ex)
			{
				CommonController.LogDebug(ex.StackTrace);
			}

			Debug.Assert(roleid > 0, $"{role} doesn't exist");
			return roleid;
		}

		public static async Task LoadRoles()
		{
			await using var connection = Database.OpenNewConnection();
			var roles = await connection.QueryAsync<Role>(@"SELECT id, name FROM roles;");
			allRoles = (List<Role>)roles;

#if DEBUG
			CommonController.LogDebug("All roles:");
			foreach (var role in allRoles)
			{
				CommonController.LogDebug($"id: {role.id} - name: {role.name}");
			}
#endif
		}

		public static async Task<bool> UserRoleExist(int userid, int roleid, NpgsqlConnection connection, HttpContext context)
		{
			try
			{
				int count = await Database.QuerySingleAsync<int>(connection, @"SELECT count(*) 
																				FROM user_roles 
																				WHERE userid = @userid AND roleid = @roleid", context, new { userid, roleid });
				return count > 0;
			}
			catch
			{
				return false;
			}
		}
	}
}
