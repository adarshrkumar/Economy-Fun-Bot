﻿using NadekoBot.Services.Database.Models;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using NadekoBot.Core.Services.Database;
using NadekoBot.Core.Services.Database.Models;
using NadekoBot.Services.Database;

namespace NadekoBot.Modules.Games.Common
{
    public static class PollExtensions
    {
        public static IEnumerable<Poll> GetAllPolls(this DbSet<Poll> polls)
        {
            return polls.Include(x => x.Answers)
                .Include(x => x.Votes)
                .ToArray();
        }

        public static void RemovePoll(this NadekoContext ctx, int id)
        {
            var p = ctx
                .Poll
                .Include(x => x.Answers)
                .Include(x => x.Votes)
                .FirstOrDefault(x => x.Id == id);

            if (p is null)
                return;
            
            if (p.Votes != null)
            {
                ctx.RemoveRange(p.Votes);
                p.Votes.Clear();
            }
            
            if (p.Answers != null)
            {
                ctx.RemoveRange(p.Answers);
                p.Answers.Clear();
            }
            
            ctx.Poll.Remove(p);
        }
    }
}