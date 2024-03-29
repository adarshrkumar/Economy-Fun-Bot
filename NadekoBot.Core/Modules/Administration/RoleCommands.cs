﻿using Discord;
using Discord.Commands;
using Discord.WebSocket;
using NadekoBot.Common;
using NadekoBot.Common.Attributes;
using NadekoBot.Core.Services.Database.Models;
using NadekoBot.Extensions;
using NadekoBot.Modules.Administration.Services;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Serilog;

namespace NadekoBot.Modules.Administration
{
    public partial class Administration
    {
        public class RoleCommands : NadekoSubmodule<RoleCommandsService>
        {
            private IServiceProvider _services;
            public enum Exclude { Excl }

            public RoleCommands(IServiceProvider services)
            {
                _services = services;
            }

            public async Task InternalReactionRoles(bool exclusive, params string[] input)
            {
                var msgs = await ((SocketTextChannel)ctx.Channel).GetMessagesAsync().FlattenAsync().ConfigureAwait(false);
                var prev = (IUserMessage)msgs.FirstOrDefault(x => x is IUserMessage && x.Id != ctx.Message.Id);

                if (prev == null)
                    return;

                if (input.Length % 2 != 0)
                    return;

                var grp = 0;
                var results = input
                    .GroupBy(x => grp++ / 2)
                    .Select(async x =>
                   {
                       var inputRoleStr = x.First();
                       var roleReader = new RoleTypeReader<SocketRole>();
                       var roleResult = await roleReader.ReadAsync(ctx, inputRoleStr, _services);
                       if (!roleResult.IsSuccess)
                       {
                           Log.Warning("Role {0} not found.", inputRoleStr);
                           return null;
                       }
                       var role = (IRole)roleResult.BestMatch;
                       if (role.Position > ((IGuildUser)ctx.User).GetRoles().Select(r => r.Position).Max()
                           && ctx.User.Id != ctx.Guild.OwnerId)
                           return null;
                       var emote = x.Last().ToIEmote();
                       return new { role, emote };
                   })
                    .Where(x => x != null);

                var all = await Task.WhenAll(results);

                if (!all.Any())
                    return;

                foreach (var x in all)
                {
                    try
                    {
                        await prev.AddReactionAsync(x.emote, new RequestOptions()
                        {
                            RetryMode = RetryMode.Retry502 | RetryMode.RetryRatelimit
                        }).ConfigureAwait(false);
                    }
                    catch (Discord.Net.HttpException ex) when(ex.HttpCode == HttpStatusCode.BadRequest)
                    {
                        await ReplyErrorLocalizedAsync("reaction_cant_access", Format.Code(x.emote.ToString()));
                        return;
                    }

                    await Task.Delay(500).ConfigureAwait(false);
                }

                if (_service.Add(ctx.Guild.Id, new ReactionRoleMessage()
                {
                    Exclusive = exclusive,
                    MessageId = prev.Id,
                    ChannelId = prev.Channel.Id,
                    ReactionRoles = all.Select(x =>
                    {
                        return new ReactionRole()
                        {
                            EmoteName = x.emote.ToString(),
                            RoleId = x.role.Id,
                        };
                    }).ToList(),
                }))
                {
                    await ctx.OkAsync();
                }
                else
                {
                    await ReplyErrorLocalizedAsync("reaction_roles_full").ConfigureAwait(false);
                }
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [NoPublicBot]
            [UserPerm(GuildPerm.ManageRoles)]
            [BotPerm(GuildPerm.ManageRoles)]
            [Priority(0)]
            public Task ReactionRoles(params string[] input) =>
                InternalReactionRoles(false, input);

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [NoPublicBot]
            [UserPerm(GuildPerm.ManageRoles)]
            [BotPerm(GuildPerm.ManageRoles)]
            [Priority(1)]
            public Task ReactionRoles(Exclude _, params string[] input) =>
                InternalReactionRoles(true, input);

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [NoPublicBot]
            [UserPerm(GuildPerm.ManageRoles)]
            public async Task ReactionRolesList()
            {
                var embed = new EmbedBuilder()
                    .WithOkColor();
                if (!_service.Get(ctx.Guild.Id, out var rrs) ||
                    !rrs.Any())
                {
                    embed.WithDescription(GetText("no_reaction_roles"));
                }
                else
                {
                    var g = ((SocketGuild)ctx.Guild);
                    foreach (var rr in rrs)
                    {
                        var ch = g.GetTextChannel(rr.ChannelId);
                        IUserMessage msg = null;
                        if (!(ch is null))
                        {
                            msg = await ch.GetMessageAsync(rr.MessageId).ConfigureAwait(false) as IUserMessage;
                        }
                        var content = msg?.Content.TrimTo(30) ?? "DELETED!";
                        embed.AddField($"**{rr.Index + 1}.** {(ch?.Name ?? "DELETED!")}",
                            GetText("reaction_roles_message", rr.ReactionRoles?.Count ?? 0, content));
                    }
                }
                await ctx.Channel.EmbedAsync(embed).ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [NoPublicBot]
            [UserPerm(GuildPerm.ManageRoles)]
            public async Task ReactionRolesRemove(int index)
            {
                if (index < 1 ||
                    !_service.Get(ctx.Guild.Id, out var rrs) ||
                    !rrs.Any() || rrs.Count < index)
                {
                    return;
                }
                index--;
                var rr = rrs[index];
                _service.Remove(ctx.Guild.Id, index);
                await ReplyConfirmLocalizedAsync("reaction_role_removed", index + 1).ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.ManageRoles)]
            [BotPerm(GuildPerm.ManageRoles)]
            public async Task SetRole(IGuildUser targetUser, [Leftover] IRole roleToAdd)
            {
                var runnerUser = (IGuildUser)ctx.User;
                var runnerMaxRolePosition = runnerUser.GetRoles().Max(x => x.Position);
                if ((ctx.User.Id != ctx.Guild.OwnerId) && runnerMaxRolePosition <= roleToAdd.Position)
                    return;
                try
                {
                    await targetUser.AddRoleAsync(roleToAdd).ConfigureAwait(false);

                    await ReplyConfirmLocalizedAsync("setrole", Format.Bold(roleToAdd.Name), Format.Bold(targetUser.ToString()))
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Error in setrole command");
                    await ReplyErrorLocalizedAsync("setrole_err").ConfigureAwait(false);
                }
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.ManageRoles)]
            [BotPerm(GuildPerm.ManageRoles)]
            public async Task RemoveRole(IGuildUser targetUser, [Leftover] IRole roleToRemove)
            {
                var runnerUser = (IGuildUser)ctx.User;
                if (ctx.User.Id != runnerUser.Guild.OwnerId && runnerUser.GetRoles().Max(x => x.Position) <= roleToRemove.Position)
                    return;
                try
                {
                    await targetUser.RemoveRoleAsync(roleToRemove).ConfigureAwait(false);
                    await ReplyConfirmLocalizedAsync("remrole", Format.Bold(roleToRemove.Name), Format.Bold(targetUser.ToString())).ConfigureAwait(false);
                }
                catch
                {
                    await ReplyErrorLocalizedAsync("remrole_err").ConfigureAwait(false);
                }
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.ManageRoles)]
            [BotPerm(GuildPerm.ManageRoles)]
            public async Task RenameRole(IRole roleToEdit, [Leftover]string newname)
            {
                var guser = (IGuildUser)ctx.User;
                if (ctx.User.Id != guser.Guild.OwnerId && guser.GetRoles().Max(x => x.Position) <= roleToEdit.Position)
                    return;
                try
                {
                    if (roleToEdit.Position > (await ctx.Guild.GetCurrentUserAsync().ConfigureAwait(false)).GetRoles().Max(r => r.Position))
                    {
                        await ReplyErrorLocalizedAsync("renrole_perms").ConfigureAwait(false);
                        return;
                    }
                    await roleToEdit.ModifyAsync(g => g.Name = newname).ConfigureAwait(false);
                    await ReplyConfirmLocalizedAsync("renrole").ConfigureAwait(false);
                }
                catch (Exception)
                {
                    await ReplyErrorLocalizedAsync("renrole_err").ConfigureAwait(false);
                }
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.ManageRoles)]
            [BotPerm(GuildPerm.ManageRoles)]
            public async Task RemoveAllRoles([Leftover] IGuildUser user)
            {
                var guser = (IGuildUser)ctx.User;

                var userRoles = user.GetRoles()
                    .Where(x => !x.IsManaged && x != x.Guild.EveryoneRole)
                    .ToList();
                
                if (user.Id == ctx.Guild.OwnerId || (ctx.User.Id != ctx.Guild.OwnerId && guser.GetRoles().Max(x => x.Position) <= userRoles.Max(x => x.Position)))
                    return;
                try
                {
                    await user.RemoveRolesAsync(userRoles).ConfigureAwait(false);
                    await ReplyConfirmLocalizedAsync("rar", Format.Bold(user.ToString())).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    await ReplyErrorLocalizedAsync("rar_err").ConfigureAwait(false);
                }
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.ManageRoles)]
            [BotPerm(GuildPerm.ManageRoles)]
            public async Task CreateRole([Leftover] string roleName = null)
            {
                if (string.IsNullOrWhiteSpace(roleName))
                    return;

                var r = await ctx.Guild.CreateRoleAsync(roleName, isMentionable: false).ConfigureAwait(false);
                await ReplyConfirmLocalizedAsync("cr", Format.Bold(r.Name)).ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.ManageRoles)]
            [BotPerm(GuildPerm.ManageRoles)]
            public async Task DeleteRole([Leftover] IRole role)
            {
                var guser = (IGuildUser)ctx.User;
                if (ctx.User.Id != guser.Guild.OwnerId
                    && guser.GetRoles().Max(x => x.Position) <= role.Position)
                    return;

                await role.DeleteAsync().ConfigureAwait(false);
                await ReplyConfirmLocalizedAsync("dr", Format.Bold(role.Name)).ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.ManageRoles)]
            [BotPerm(GuildPerm.ManageRoles)]
            public async Task RoleHoist(IRole role)
            {
                var newHoisted = !role.IsHoisted;
                await role.ModifyAsync(r => r.Hoist = newHoisted).ConfigureAwait(false);
                if (newHoisted)
                {
                    await ReplyConfirmLocalizedAsync("rolehoist_enabled", Format.Bold(role.Name)).ConfigureAwait(false);
                }
                else
                {
                    await ReplyConfirmLocalizedAsync("rolehoist_disabled", Format.Bold(role.Name)).ConfigureAwait(false);
                }
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [Priority(1)]
            public async Task RoleColor([Leftover] IRole role)
            {
                await ctx.Channel.SendConfirmAsync("Role Color", role.Color.RawValue.ToString("x6")).ConfigureAwait(false);
            }

            [NadekoCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.ManageRoles)]
            [BotPerm(GuildPerm.ManageRoles)]
            [Priority(0)]
            public async Task RoleColor(SixLabors.ImageSharp.Color color, [Leftover]IRole role)
            {
                try
                {
                    var rgba32 = color.ToPixel<Rgba32>();
                    await role.ModifyAsync(r => r.Color = new Color(rgba32.R, rgba32.G, rgba32.B)).ConfigureAwait(false);
                    await ReplyConfirmLocalizedAsync("rc", Format.Bold(role.Name)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    await ReplyErrorLocalizedAsync("rc_perms").ConfigureAwait(false);
                }
            }
        }
    }
}