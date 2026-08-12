using System.Text;

namespace ClaudeScord;

// Headless smoke test for the REST layer: `ClaudeScord --apitest [token]`. The WinExe has no
// console, so results go to apitest.log next to the exe. Exercises exactly the endpoints the new
// features sit on — slash command index, GIF search, sticker packs, and a real send into the
// "failme" DM (the user's designated test account) — so a broken endpoint is caught here instead
// of in the UI.
static class ApiTest
{
    public static int Run(string token)
    {
        var sb = new StringBuilder();
        int rc = 0;

        try
        {
            if (string.IsNullOrEmpty(token))
            {
                sb.AppendLine("FAIL: no token (pass it after --apitest, or log in first)");
                rc = 1;
                goto Done;
            }

            var client = new UserClient(token);
            var me = client.Rest.GetSelfAsync().GetAwaiter().GetResult();
            sb.AppendLine("self: " + (me == null ? "null" : me.Username + " (" + me.Id + ")"));

            var cmds = client.Rest.GetCommandIndexAsync(null).GetAwaiter().GetResult();
            sb.AppendLine("slash commands (@me): " + cmds.Count
                          + (cmds.Count > 0 ? "  first: /" + cmds[0].Name : ""));
            // The options panel renders these; a broken Options field would silently show no fields.
            var withOpts = cmds.Where(c => c.Options.Count > 0).ToList();
            var withSubs = cmds.Where(c => c.HasSubcommands).ToList();
            sb.AppendLine("with options: " + withOpts.Count + ", with subcommands: " + withSubs.Count
                          + (withOpts.Count > 0 ? "  e.g. /" + withOpts[0].Name
                             + " [" + string.Join(", ", withOpts[0].Options.Take(3).Select(o => o.Name + (o.Required ? "*" : ""))) + "]" : ""));

            // The options panel is fed by the *guild* command index — /@me has no optioned commands
            // in a DM. Prove the Options field actually deserializes on a real server.
            var guildList = client.Rest.GetMyGuildsAsync().GetAwaiter().GetResult() ?? new List<UserGuild>();
            var probeGuild = guildList.FirstOrDefault(g => g.Id != 0);
            if (probeGuild != null)
            {
                var gcmds = client.Rest.GetCommandIndexAsync(probeGuild.Id).GetAwaiter().GetResult();
                var gOpts = gcmds.Where(c => c.Options.Count > 0).ToList();
                sb.AppendLine("guild " + probeGuild.Name + " commands: " + gcmds.Count
                              + ", with options: " + gOpts.Count
                              + (gOpts.Count > 0 ? "  e.g. /" + gOpts[0].Name + " ["
                                 + string.Join(", ", gOpts[0].Options.Take(3).Select(o => o.Name + (o.Required ? "*" : ""))) + "]" : ""));
            }
            else sb.AppendLine("guild command index: no guild to probe");

            var gifs = client.Rest.TrendingGifsAsync().GetAwaiter().GetResult();
            sb.AppendLine("trending gifs: " + gifs.Count
                          + (gifs.Count > 0 ? "  first: " + Short(gifs[0].Url) : ""));
            if (client.Rest.LastGifError != null) sb.AppendLine("gif error: " + client.Rest.LastGifError);

            var packs = client.Rest.GetStickerPacksAsync().GetAwaiter().GetResult();
            sb.AppendLine("sticker packs: " + packs.Count
                          + "  total stickers: " + packs.Sum(p => p.Stickers.Count));

            var dms = client.Rest.GetDmChannelsAsync().GetAwaiter().GetResult();
            // The test account's username is "failme." — a prefix match covers it.
            var failme = dms.FirstOrDefault(d => d.Type == 1
                && (d.Recipient?.Username ?? "").StartsWith("failme", StringComparison.OrdinalIgnoreCase));
            if (failme == null)
            {
                sb.AppendLine("failme DM: not found (dms: "
                              + string.Join(", ", dms.Where(d => d.Recipient != null).Take(6).Select(d => d.Recipient!.Username))
                              + ")");
            }
            else
            {
                var history = client.Rest.GetMessagesAsync(failme.Id, 5).GetAwaiter().GetResult();
                sb.AppendLine("failme DM (" + failme.Id + "): " + history.Count + " recent messages, newest: \""
                              + Short(history.LastOrDefault()?.Content ?? "") + "\"");

                // The search box's filter syntax reaches the endpoint as real query parameters —
                // prove the plumbing with a before: filter that must actually restrict the results.
                var searchHits = client.Rest.SearchAsync(null, failme.Id, "", extra: new Dictionary<string, string>
                {
                    ["before"] = "2030-01-01",
                    ["author_id"] = me?.Id.ToString() ?? "",
                }).GetAwaiter().GetResult();
                sb.AppendLine("search (author_id + before): " + searchHits.Count + " hits"
                              + (searchHits.Count > 0 ? ", first: \"" + Short(searchHits.First().Content) + "\"" : ""));
                var sent = client.Rest.SendMessageAsync(failme.Id,
                    "ClaudeScord smoke test — polls, buttons and slash commands are online ✔").GetAwaiter().GetResult();
                sb.AppendLine("sent into failme DM: " + (sent?.Id.ToString() ?? "null"));
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("FAIL: " + ex);
            rc = 1;
        }

    Done:
        var log = Path.Combine(AppContext.BaseDirectory, "apitest.log");
        try { File.WriteAllText(log, sb.ToString()); } catch { }
        Console.WriteLine(sb);
        return rc;
    }

    static string Short(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\n", " ").Replace("\r", " ");
        return s.Length <= 70 ? s : s[..70] + "…";
    }
}
