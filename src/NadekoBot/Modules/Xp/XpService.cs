#nullable disable warnings
using LinqToDB;
using Microsoft.EntityFrameworkCore;
using NadekoBot.Common.ModuleBehaviors;
using NadekoBot.Db.Models;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using LinqToDB.Data;
using LinqToDB.EntityFrameworkCore;
using LinqToDB.Tools;
using Microsoft.Extensions.Caching.Memory;
using NadekoBot.Modules.Administration;
using NadekoBot.Modules.Patronage;
using ArgumentOutOfRangeException = System.ArgumentOutOfRangeException;
using Color = SixLabors.ImageSharp.Color;
using Exception = System.Exception;
using Image = SixLabors.ImageSharp.Image;

namespace NadekoBot.Modules.Xp.Services;

public class XpService : INService, IReadyExecutor, IExecNoCommand
{
    private readonly DbService _db;
    private readonly IImageCache _images;
    private readonly FontProvider _fonts;
    private readonly IBotCreds _creds;
    private readonly ICurrencyService _cs;
    private readonly IHttpClientFactory _httpFactory;
    private readonly XpConfigService _xpConfig;

    private readonly DiscordSocketClient _client;

    private readonly IPatronageService _ps;
    private readonly IBotCache _c;

    private readonly INotifySubscriber _notifySub;
    private readonly IMemoryCache _memCache;
    private readonly XpTemplateService _templateService;
    private readonly XpRateService _xpRateRateService;
    private readonly XpExclusionService _xpExcl;

    private readonly QueueRunner _levelUpQueue = new QueueRunner(0, 100);

    public XpService(
        DiscordSocketClient client,
        DbService db,
        IImageCache images,
        IBotCache c,
        FontProvider fonts,
        IBotCreds creds,
        ICurrencyService cs,
        IHttpClientFactory http,
        XpConfigService xpConfig,
        IPubSub pubSub,
        IPatronageService ps,
        INotifySubscriber notifySub,
        IMemoryCache memCache,
        ShardData shardData,
        XpTemplateService templateService,
        XpRateService xpRateRateService,
        XpExclusionService xpExcl
    )
    {
        _db = db;
        _images = images;
        _fonts = fonts;
        _creds = creds;
        _cs = cs;
        _httpFactory = http;
        _xpConfig = xpConfig;
        _notifySub = notifySub;
        _memCache = memCache;
        _templateService = templateService;
        _xpRateRateService = xpRateRateService;
        _xpExcl = xpExcl;
        _client = client;
        _ps = ps;
        _c = c;
    }

    public async Task OnReadyAsync()
    {
        await Task.WhenAll(UpdateTimer(), VoiceUpdateTimer(), _levelUpQueue.RunAsync());

        return;

        async Task VoiceUpdateTimer()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            while (await timer.WaitForNextTickAsync())
            {
                try
                {
                    await UpdateVoiceXp();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error updating voice xp");
                }
            }
        }

        async Task UpdateTimer()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync())
            {
                try
                {
                    await UpdateXp();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error updating xp");
                    await Task.Delay(30_000);
                }
            }
        }
    }

    /// <summary>
    /// The current batch of users that will gain xp
    /// </summary>
    private readonly ConcurrentHashSet<XpQueueEntry> _usersBatch = [];

    /// <summary>
    /// The current batch of users that will gain voice xp
    /// </summary>
    private readonly HashSet<IGuildUser> _voiceXpBatch = [];

    private async Task UpdateVoiceXp()
    {
        var oldBatch = _voiceXpBatch.ToHashSet();
        _voiceXpBatch.Clear();
        var validUsers = new List<XpQueueEntry>(oldBatch.Count);

        var guilds = _client.Guilds;

        foreach (var g in guilds)
        {
            foreach (var vc in g.VoiceChannels)
            {
                if (!IsVoiceChannelActive(vc))
                    continue;

                var (rate, _) = _xpRateRateService.GetXpRate(XpRateType.Voice, g.Id, vc.Id);

                if (rate.IsExcluded())
                    continue;

                foreach (var u in vc.ConnectedUsers)
                {
                    if (!UserParticipatingInVoiceChannel(u))
                        continue;

                    if (IsUserExcluded(g, u))
                        continue;

                    if (oldBatch.Contains(u))
                    {
                        validUsers.Add(new(u, rate.Amount, vc.Id));
                    }

                    _voiceXpBatch.Add(u);
                }
            }
        }

        await UpdateXpInternalAsync(validUsers.ToArray());
    }

    private async Task UpdateXp()
    {
        // might want to lock this, but it's not a big deal

        // or do something like this
        // foreach (var item in currentBatch)
        //     _usersBatch.TryRemove(item);

        var currentBatch = _usersBatch.ToArray();
        _usersBatch.Clear();

        await UpdateXpInternalAsync(currentBatch);
    }

    private async Task UpdateXpInternalAsync(XpQueueEntry[] currentBatch)
    {
        if (currentBatch.Length == 0)
            return;

        await using var ctx = _db.GetDbContext();
        await using var lctx = ctx.CreateLinqToDBConnection();

        var tempTableName = "xptemp_" + Guid.NewGuid().ToString().Replace("-", string.Empty);
        await using var batchTable = await lctx.CreateTempTableAsync<UserXpBatch>(tempTableName);

        await batchTable.BulkCopyAsync(currentBatch.Select(x => new UserXpBatch()
        {
            GuildId = x.User.GuildId,
            UserId = x.User.Id,
            Username = x.User.Username,
            AvatarId = x.User.DisplayAvatarId,
            XpToGain = x.Xp
        }));

        await lctx.ExecuteAsync(
            $"""
             INSERT INTO UserXpStats (GuildId, UserId, Xp)
             SELECT "{tempTableName}"."GuildId", "{tempTableName}"."UserId", "XpToGain"
             FROM {tempTableName}
             WHERE TRUE
             ON CONFLICT (GuildId, UserId) DO UPDATE 
             SET 
                 Xp = UserXpStats.Xp + EXCLUDED.Xp;
             """);


        var updated = await batchTable
            .InnerJoin(lctx.GetTable<UserXpStats>(),
                (u, s) => u.GuildId == s.GuildId && u.UserId == s.UserId,
                (batch, stats) => stats)
            .ToListAsyncLinqToDB();

        var userToXp = currentBatch.ToDictionary(x => x.User.Id, x => x);
        foreach (var u in updated)
        {
            if (!userToXp.TryGetValue(u.UserId, out var data))
                continue;

            var oldStats = new LevelStats(u.Xp - data.Xp);
            var newStats = new LevelStats(u.Xp);

            if (oldStats.Level < newStats.Level)
            {
                await _levelUpQueue.EnqueueAsync(NotifyUser(u.GuildId,
                    data.ChannelId,
                    u.UserId,
                    oldStats.Level,
                    newStats.Level));
            }
        }
    }

    private Func<Task> NotifyUser(
        ulong guildId,
        ulong? channelId,
        ulong userId,
        long oldLevel,
        long newLevel)
        => async () =>
        {
            await HandleRewardsInternalAsync(guildId, userId, oldLevel, newLevel);

            await HandleNotifyInternalAsync(guildId, channelId, userId, newLevel);
        };

    private async Task HandleRewardsInternalAsync(
        ulong guildId,
        ulong userId,
        long oldLevel,
        long newLevel)
    {
        var settings = await GetFullXpSettingsFor(guildId);
        var rrews = settings.RoleRewards;
        var crews = settings.CurrencyRewards;

        //loop through levels since last level up, so if a high amount of xp is gained, reward are still applied.
        for (var i = oldLevel + 1; i <= newLevel; i++)
        {
            var rrew = rrews.FirstOrDefault(x => x.Level == i);
            if (rrew is not null)
            {
                var guild = _client.GetGuild(guildId);
                var role = guild?.GetRole(rrew.RoleId);
                var user = guild?.GetUser(userId);

                if (role is not null && user is not null)
                {
                    if (rrew.Remove)
                    {
                        try
                        {
                            await user.RemoveRoleAsync(role);
                            await _notifySub.NotifyAsync(new RemoveRoleRewardNotifyModel(guild.Id,
                                    role.Id,
                                    user.Id,
                                    newLevel),
                                isShardLocal: true);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex,
                                "Unable to remove role {RoleId} from user {UserId}: {Message}",
                                role.Id,
                                user.Id,
                                ex.Message);
                        }
                    }
                    else
                    {
                        try
                        {
                            await user.AddRoleAsync(role);
                            await _notifySub.NotifyAsync(new AddRoleRewardNotifyModel(guild.Id,
                                    role.Id,
                                    user.Id,
                                    newLevel),
                                isShardLocal: true);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex,
                                "Unable to add role {RoleId} to user {UserId}: {Message}",
                                role.Id,
                                user.Id,
                                ex.Message);
                        }
                    }
                }
            }

            //get currency reward for this level
            var crew = crews.FirstOrDefault(x => x.Level == i);
            if (crew is not null)
            {
                //give the user the reward if it exists
                await _cs.AddAsync(userId, crew.Amount, new("xp", "level-up"));
            }
        }
    }

    private async Task HandleNotifyInternalAsync(
        ulong guildId,
        ulong? channelId,
        ulong userId,
        long newLevel)
    {
        var guild = _client.GetGuild(guildId);
        var user = guild?.GetUser(userId);

        if (guild is null || user is null)
            return;

        var model = new LevelUpNotifyModel()
        {
            GuildId = guildId,
            UserId = userId,
            ChannelId = channelId,
            Level = newLevel
        };
        await _notifySub.NotifyAsync(model, true);
        return;
    }

    public async Task SetCurrencyReward(ulong guildId, int level, int amount)
    {
        await using var uow = _db.GetDbContext();
        var settings = await uow.XpSettingsFor(guildId, set => set.LoadWith(x => x.CurrencyRewards));

        if (amount <= 0)
        {
            var toRemove = settings.CurrencyRewards.FirstOrDefault(x => x.Level == level);
            if (toRemove is not null)
            {
                uow.Remove(toRemove);
                settings.CurrencyRewards.Remove(toRemove);
            }
        }
        else
        {
            var rew = settings.CurrencyRewards.FirstOrDefault(x => x.Level == level);

            if (rew is not null)
                rew.Amount = amount;
            else
            {
                settings.CurrencyRewards.Add(new()
                {
                    Level = level,
                    Amount = amount
                });
            }
        }

        await uow.SaveChangesAsync();
    }

    public async Task<XpSettings> GetFullXpSettingsFor(ulong guildId)
    {
        await using var uow = _db.GetDbContext();
        return await uow.XpSettingsFor(guildId,
            set => set
                .LoadWith(x => x.CurrencyRewards)
                .LoadWith(x => x.RoleRewards));
    }

    public async Task ResetRoleRewardAsync(ulong guildId, int level)
    {
        await using var uow = _db.GetDbContext();
        var settings = await uow.XpSettingsFor(guildId, set => set.LoadWith(x => x.RoleRewards));

        var toRemove = settings.RoleRewards.FirstOrDefault(x => x.Level == level);
        if (toRemove is not null)
        {
            uow.Remove(toRemove);
            settings.RoleRewards.Remove(toRemove);
        }

        await uow.SaveChangesAsync();
    }

    public async Task SetRoleRewardAsync(
        ulong guildId,
        int level,
        ulong roleId,
        bool remove)
    {
        await using var uow = _db.GetDbContext();
        var settings = await uow.XpSettingsFor(guildId, set => set.LoadWith(x => x.RoleRewards));

        var rew = settings.RoleRewards.FirstOrDefault(x => x.Level == level);

        if (rew is not null)
        {
            rew.RoleId = roleId;
            rew.Remove = remove;
        }
        else
        {
            settings.RoleRewards.Add(new()
            {
                Level = level,
                RoleId = roleId,
                Remove = remove,
            });
        }

        await uow.SaveChangesAsync();
    }

    public async Task<IReadOnlyCollection<UserXpStats>> GetGuildUserXps(ulong guildId, int page)
    {
        await using var uow = _db.GetDbContext();
        return await uow
            .UserXpStats
            .Where(x => x.GuildId == guildId)
            .OrderByDescending(x => x.Xp)
            .Skip(page * 10)
            .Take(10)
            .ToArrayAsyncLinqToDB();
    }

    public async Task<IReadOnlyCollection<UserXpStats>> GetGuildUserXps(ulong guildId, List<ulong> users, int page)
    {
        await using var uow = _db.GetDbContext();
        return await uow.Set<UserXpStats>()
            .Where(x => x.GuildId == guildId && x.UserId.In(users))
            .OrderByDescending(x => x.Xp)
            .Skip(page * 10)
            .Take(10)
            .ToArrayAsyncLinqToDB();
    }

    private bool IsVoiceChannelActive(SocketVoiceChannel channel)
    {
        var count = 0;
        foreach (var user in channel.ConnectedUsers)
        {
            if (UserParticipatingInVoiceChannel(user))
            {
                if (++count >= 2)
                    return true;
            }
        }

        return false;
    }

    private static bool UserParticipatingInVoiceChannel(SocketGuildUser user)
        => !user.IsDeafened && !user.IsMuted && !user.IsSelfDeafened && !user.IsSelfMuted;

    public Task ExecOnNoCommandAsync(IGuild guild, IUserMessage arg)
    {
        if (arg.Author is not SocketGuildUser user || user.IsBot)
            return Task.CompletedTask;

        if (arg.Channel is not IGuildChannel gc)
            return Task.CompletedTask;

        _ = Task.Run(async () =>
        {
            if (IsUserExcluded(guild, user))
                return;

            var isImage = arg.Attachments.Any(a => a.Height >= 16 && a.Width >= 16);
            var isText = arg.Content.Contains(' ') || arg.Content.Length >= 5;

            // try to get a rate for this channel
            // and if there is no specified rate, use thread rate
            var (textRate, isItemRate) = _xpRateRateService.GetXpRate(XpRateType.Text, guild.Id, gc.Id);
            if (!isItemRate && gc is SocketThreadChannel tc)
            {
                (textRate, _) = _xpRateRateService.GetXpRate(XpRateType.Text, guild.Id, tc.ParentChannel.Id);
            }

            XpRate rate;
            if (isImage)
            {
                var (imageRate, _) = _xpRateRateService.GetXpRate(XpRateType.Image, guild.Id, gc.Id);
                if (imageRate.IsExcluded())
                    return;

                rate = imageRate;
            }
            else if (isText)
            {
                if (textRate.IsExcluded())
                    return;

                rate = textRate;
            }
            else
            {
                return;
            }

            if (!await TryAddUserGainedXpAsync(user.Id, rate.Cooldown))
                return;

            _usersBatch.Add(new(user, rate.Amount, gc.Id));
        });

        return Task.CompletedTask;
    }

    private bool IsUserExcluded(IGuild guild, SocketGuildUser user)
    {
        if (_xpExcl.IsExcluded(guild.Id, XpExcludedItemType.User, user.Id))
            return true;

        foreach (var role in user.Roles)
        {
            if (_xpExcl.IsExcluded(guild.Id, XpExcludedItemType.Role, role.Id))
                return true;
        }

        return false;
    }

    public Task AddXpAsync(ulong channelId, long amount, params IGuildUser[] users)
    {
        foreach(var user in users)
            _usersBatch.Add(new(user, amount, channelId));
        
        return Task.CompletedTask;
    }
    
    private Task<bool> TryAddUserGainedXpAsync(ulong userId, float cdInMinutes)
    {
        if (cdInMinutes <= float.Epsilon)
            return Task.FromResult(true);

        if (_memCache.TryGetValue("xp_gain:" + userId, out _))
            return Task.FromResult(false);

        using var entry = _memCache.CreateEntry("xp_gain:" + userId);
        entry.Value = true;

        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(cdInMinutes);

        return Task.FromResult(true);
    }

    public async Task<FullUserStats> GetUserStatsAsync(IGuildUser user)
    {
        await using var uow = _db.GetDbContext();
        var du = uow.GetOrCreateUser(user, set => set.Include(x => x.Club));
        var guildRank = await uow.Set<UserXpStats>().GetUserGuildRanking(user.Id, user.GuildId);
        var stats = uow.GetOrCreateUserXpStats(user.GuildId, user.Id);
        await uow.SaveChangesAsync();

        return new(du,
            stats,
            new(stats.Xp),
            guildRank);
    }

    public async Task<(Stream Image, IImageFormat Format)> GenerateXpImageAsync(IGuildUser user)
    {
        var stats = await GetUserStatsAsync(user);
        return await GenerateXpImageAsync(stats);
    }

    public Task<(Stream Image, IImageFormat Format)> GenerateXpImageAsync(FullUserStats stats)
        => Task.Run(async () =>
        {
            var template = _templateService.GetTemplate();
            var bgBytes = await GetXpBackgroundAsync(stats.User.UserId);

            if (bgBytes is null)
            {
                Log.Warning("Xp background image could not be loaded");
                throw new ArgumentNullException(nameof(bgBytes));
            }

            var avatarUrl = stats.User.RealAvatarUrl();
            var avatarFetchTask = Task.Run(async () =>
            {
                try
                {
                    if (avatarUrl is null)
                        return null;

                    var result = await _c.GetImageDataAsync(avatarUrl);
                    if (result.TryPickT0(out var imgData, out _))
                        return imgData;

                    using var http = _httpFactory.CreateClient();

                    var avatarData = await http.GetByteArrayAsync(avatarUrl);
                    using var tempDraw = Image.Load<Rgba32>(avatarData);

                    tempDraw.Mutate(x => x
                        .Resize(template.User.Icon.Size.X, template.User.Icon.Size.Y)
                        .ApplyRoundedCorners(Math.Max(template.User.Icon.Size.X,
                                                 template.User.Icon.Size.Y)
                                             / 2.0f));
                    await using var stream = await tempDraw.ToStreamAsync();
                    var data = stream.ToArray();
                    await _c.SetImageDataAsync(avatarUrl, data);
                    return data;
                }
                catch (Exception)
                {
                    return null;
                }
            });

            using var img = Image.Load<Rgba32>(bgBytes);


            img.Mutate(x =>
            {
                if (template.User.Name.Show)
                {
                    var fontSize = (int)(template.User.Name.FontSize * 0.9);
                    var username = stats.User.ToString();
                    var usernameFont = _fonts.NotoSans.CreateFont(fontSize, FontStyle.Bold);

                    var size = TextMeasurer.MeasureSize($"@{username}", new(usernameFont));
                    var scale = 400f / size.Width;
                    if (scale < 1)
                        usernameFont = _fonts.NotoSans.CreateFont(template.User.Name.FontSize * scale, FontStyle.Bold);

                    x.DrawText(new RichTextOptions(usernameFont)
                        {
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Center,
                            FallbackFontFamilies = _fonts.FallBackFonts,
                            Origin = new(template.User.Name.Pos.X, template.User.Name.Pos.Y + 8)
                        },
                        "@" + username,
                        Brushes.Solid(template.User.Name.Color));
                }


                //club name

                if (template.Club.Name.Show)
                {
                    var clubName = stats.User.Club?.ToString() ?? "-";

                    var clubFont = _fonts.NotoSans.CreateFont(template.Club.Name.FontSize, FontStyle.Regular);

                    x.DrawText(new RichTextOptions(clubFont)
                        {
                            HorizontalAlignment = HorizontalAlignment.Right,
                            VerticalAlignment = VerticalAlignment.Top,
                            FallbackFontFamilies = _fonts.FallBackFonts,
                            Origin = new(template.Club.Name.Pos.X + 50, template.Club.Name.Pos.Y - 8)
                        },
                        clubName,
                        Brushes.Solid(template.Club.Name.Color));
                }

                Font GetTruncatedFont(
                    FontFamily fontFamily,
                    int fontSize,
                    FontStyle style,
                    string text,
                    int maxSize)
                {
                    var font = fontFamily.CreateFont(fontSize, style);
                    var size = TextMeasurer.MeasureSize(text, new(font));
                    var scale = maxSize / size.Width;
                    if (scale < 1)
                        font = fontFamily.CreateFont(fontSize * scale, style);

                    return font;
                }


                if (template.User.Level.Show)
                {
                    var guildLevelFont = GetTruncatedFont(
                        _fonts.NotoSans,
                        template.User.Level.FontSize,
                        FontStyle.Bold,
                        stats.Guild.Level.ToString(),
                        33);


                    x.DrawText(stats.Guild.Level.ToString(),
                        guildLevelFont,
                        template.User.Level.Color,
                        new(template.User.Level.Pos.X, template.User.Level.Pos.Y));
                }


                var guild = stats.Guild;

                //xp bar
                if (template.User.Xp.Bar.Show)
                {
                    var xpPercent = guild.LevelXp / (float)guild.RequiredXp;
                    DrawXpBar(xpPercent, template.User.Xp.Bar.Guild, img);
                }

                if (template.User.Xp.Guild.Show)
                {
                    x.DrawText(
                        new RichTextOptions(_fonts.NotoSans.CreateFont(template.User.Xp.Guild.FontSize,
                            FontStyle.Bold))
                        {
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            Origin = new(template.User.Xp.Guild.Pos.X, template.User.Xp.Guild.Pos.Y)
                        },
                        $"{guild.LevelXp}/{guild.RequiredXp}",
                        Brushes.Solid(template.User.Xp.Guild.Color));
                }

                var rankPen = new SolidPen(Color.White, 1);
                //ranking

                if (template.User.Rank.Show)
                {
                    var guildRankStr = stats.GuildRanking.ToString();

                    var guildRankFont = GetTruncatedFont(
                        _fonts.NotoSans,
                        template.User.Rank.FontSize,
                        FontStyle.Bold,
                        guildRankStr,
                        22);

                    x.DrawText(
                        new RichTextOptions(guildRankFont)
                        {
                            Origin = new(template.User.Rank.Pos.X, template.User.Rank.Pos.Y)
                        },
                        guildRankStr,
                        Brushes.Solid(template.User.Rank.Color),
                        rankPen
                    );
                }
            });

            if (template.User.Icon.Show)
            {
                var avImageData = await avatarFetchTask;
                img.Mutate(mut =>
                {
                    try
                    {
                        using var toDraw = Image.Load(avImageData);
                        if (toDraw.Size != new Size(template.User.Icon.Size.X, template.User.Icon.Size.Y))
                            toDraw.Mutate(x => x.Resize(template.User.Icon.Size.X, template.User.Icon.Size.Y));

                        mut.DrawImage(toDraw,
                            new Point(template.User.Icon.Pos.X, template.User.Icon.Pos.Y),
                            1);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Error drawing avatar image");
                    }
                });
            }

            //club image
            if (template.Club.Icon.Show)
                await DrawClubImage(template, img, stats);

            await DrawFrame(img, stats.User.UserId);

            var outputSize = template.OutputSize;
            if (outputSize.X != img.Width || outputSize.Y != img.Height)
                img.Mutate(x => x.Resize(template.OutputSize.X, template.OutputSize.Y));

            var imageFormat = img.Metadata.DecodedImageFormat;
            var output = ((Stream)await img.ToStreamAsync(imageFormat), imageFormat);

            return output;
        });

    private async Task<byte[]?> GetXpBackgroundAsync(ulong userId)
    {
        var item = await GetItemInUse(userId, XpShopItemType.Background);
        if (item is null)
        {
            return await _images.GetXpBackgroundImageAsync();
        }

        var url = _xpConfig.Data.Shop.GetItemUrl(XpShopItemType.Background, item.ItemKey);
        if (!string.IsNullOrWhiteSpace(url))
        {
            var data = await _images.GetImageDataAsync(new Uri(url));
            return data;
        }

        return await _images.GetXpBackgroundImageAsync();
    }

    private async Task DrawFrame(Image<Rgba32> img, ulong userId)
    {
        var item = await GetItemInUse(userId, XpShopItemType.Frame);

        Image? frame = null;
        if (item is not null)
        {
            var url = _xpConfig.Data.Shop.GetItemUrl(XpShopItemType.Frame, item.ItemKey);
            if (!string.IsNullOrWhiteSpace(url))
            {
                var data = await _images.GetImageDataAsync(new Uri(url));
                frame = Image.Load<Rgba32>(data);
            }
        }

        if (frame is not null)
            img.Mutate(x => x.DrawImage(frame, new Point(0, 0), new GraphicsOptions()));
    }

    private void DrawXpBar(float percent, XpBar info, Image<Rgba32> img)
    {
        var x1 = info.PointA.X;
        var y1 = info.PointA.Y;

        var x2 = info.PointB.X;
        var y2 = info.PointB.Y;

        var length = info.Length * percent;

        float x3, x4, y3, y4;

        var matrix = info.Direction switch
        {
            XpTemplateDirection.Down => new float[,] { { 0, 1 }, { 0, 1 } },
            XpTemplateDirection.Up => new float[,] { { 0, -1 }, { 0, -1 } },
            XpTemplateDirection.Left => new float[,] { { -1, 0 }, { -1, 0 } },
            _ => new float[,] { { 1, 0 }, { 1, 0 } },
        };

        x3 = x1 + matrix[0, 0] * length;
        x4 = x2 + matrix[1, 0] * length;
        y3 = y1 + matrix[0, 1] * length;
        y4 = y2 + matrix[1, 1] * length;

        img.Mutate(x => x.FillPolygon(info.Color,
            new PointF(x1, y1),
            new PointF(x3, y3),
            new PointF(x4, y4),
            new PointF(x2, y2)));
    }

    private async Task DrawClubImage(XpTemplate template, Image<Rgba32> img, FullUserStats stats)
    {
        if (!string.IsNullOrWhiteSpace(stats.User.Club?.ImageUrl))
        {
            try
            {
                var imgUrl = new Uri(stats.User.Club.ImageUrl);
                var result = await _c.GetImageDataAsync(imgUrl);
                if (!result.TryPickT0(out var data, out _))
                {
                    using (var http = _httpFactory.CreateClient())
                    using (var temp = await http.GetAsync(imgUrl, HttpCompletionOption.ResponseHeadersRead))
                    {
                        if (!temp.IsImage() || temp.GetContentLength() > 11 * 1024 * 1024)
                            return;

                        var imgData = await temp.Content.ReadAsByteArrayAsync();
                        using (var tempDraw = Image.Load<Rgba32>(imgData))
                        {
                            tempDraw.Mutate(x => x
                                .Resize(template.Club.Icon.Size.X, template.Club.Icon.Size.Y)
                                .ApplyRoundedCorners(Math.Max(template.Club.Icon.Size.X,
                                                         template.Club.Icon.Size.Y)
                                                     / 2.0f));
                            await using (var tds = await tempDraw.ToStreamAsync())
                            {
                                data = tds.ToArray();
                            }
                        }
                    }

                    await _c.SetImageDataAsync(imgUrl, data);
                }

                using var toDraw = Image.Load(data);
                if (toDraw.Size != new Size(template.Club.Icon.Size.X, template.Club.Icon.Size.Y))
                    toDraw.Mutate(x => x.Resize(template.Club.Icon.Size.X, template.Club.Icon.Size.Y));

                img.Mutate(x => x.DrawImage(
                    toDraw,
                    new Point(template.Club.Icon.Pos.X, template.Club.Icon.Pos.Y),
                    1));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error drawing club image");
            }
        }
    }

    public async Task XpReset(ulong guildId, ulong userId)
    {
        await using var uow = _db.GetDbContext();
        await uow.GetTable<UserXpStats>()
            .DeleteAsync(x => x.UserId == userId && x.GuildId == guildId);
    }

    public void XpReset(ulong guildId)
    {
        using var uow = _db.GetDbContext();
        uow.Set<UserXpStats>().ResetGuildXp(guildId);
        uow.SaveChanges();
    }

    public async Task ResetXpRewards(ulong guildId)
    {
        await using var uow = _db.GetDbContext();
        await uow.GetTable<XpSettings>()
            .Where(x => x.GuildId == guildId)
            .DeleteAsync();
    }

    public ValueTask<Dictionary<string, XpConfig.ShopItemInfo>?> GetShopBgs()
    {
        var data = _xpConfig.Data;
        if (!data.Shop.IsEnabled)
            return new(default(Dictionary<string, XpConfig.ShopItemInfo>));

        return new(_xpConfig.Data.Shop.Bgs?.Where(x => x.Value.Price >= 0)
            .ToDictionary(x => x.Key, x => x.Value));
    }

    public ValueTask<Dictionary<string, XpConfig.ShopItemInfo>?> GetShopFrames()
    {
        var data = _xpConfig.Data;
        if (!data.Shop.IsEnabled)
            return new(default(Dictionary<string, XpConfig.ShopItemInfo>));

        return new(_xpConfig.Data.Shop.Frames?.Where(x => x.Value.Price >= 0)
            .ToDictionary(x => x.Key, x => x.Value));
    }

    public async Task<BuyResult> BuyShopItemAsync(ulong userId, XpShopItemType type, string key)
    {
        var conf = _xpConfig.Data;

        if (!conf.Shop.IsEnabled)
            return BuyResult.XpShopDisabled;

        await using var ctx = _db.GetDbContext();
        try
        {
            if (await ctx.GetTable<XpShopOwnedItem>()
                    .AnyAsyncLinqToDB(x => x.UserId == userId && x.ItemKey == key && x.ItemType == type))
                return BuyResult.AlreadyOwned;

            var item = GetShopItem(type, key);

            if (item is null || item.Price < 0)
                return BuyResult.UnknownItem;

            if (item.Price > 0 && !await _cs.RemoveAsync(userId, item.Price, new("xpshop", "buy", $"Background {key}")))
                return BuyResult.InsufficientFunds;


            await ctx.GetTable<XpShopOwnedItem>()
                .InsertAsync(() => new XpShopOwnedItem()
                {
                    UserId = userId,
                    IsUsing = false,
                    ItemKey = key,
                    ItemType = type,
                    DateAdded = DateTime.UtcNow,
                });

            return BuyResult.Success;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error buying shop item: {ErrorMessage}", ex.Message);
            return BuyResult.UnknownItem;
        }
    }

    private XpConfig.ShopItemInfo? GetShopItem(XpShopItemType type, string key)
    {
        var data = _xpConfig.Data;
        if (type == XpShopItemType.Background)
        {
            if (data.Shop.Bgs is { } bgs && bgs.TryGetValue(key, out var item))
                return item;

            return null;
        }

        if (type == XpShopItemType.Frame)
        {
            if (data.Shop.Frames is { } fs && fs.TryGetValue(key, out var item))
                return item;

            return null;
        }

        throw new ArgumentOutOfRangeException(nameof(type));
    }

    public async Task<bool> OwnsItemAsync(
        ulong userId,
        XpShopItemType itemType,
        string key)
    {
        await using var ctx = _db.GetDbContext();
        return await ctx.GetTable<XpShopOwnedItem>()
            .AnyAsyncLinqToDB(x => x.UserId == userId
                                   && x.ItemType == itemType
                                   && x.ItemKey == key);
    }


    public async Task<XpShopOwnedItem?> GetUserItemAsync(
        ulong userId,
        XpShopItemType itemType,
        string key)
    {
        await using var ctx = _db.GetDbContext();
        return await ctx.GetTable<XpShopOwnedItem>()
            .FirstOrDefaultAsyncLinqToDB(x => x.UserId == userId
                                              && x.ItemType == itemType
                                              && x.ItemKey == key);
    }

    public async Task<XpShopOwnedItem?> GetItemInUse(
        ulong userId,
        XpShopItemType itemType)
    {
        await using var ctx = _db.GetDbContext();
        return await ctx.GetTable<XpShopOwnedItem>()
            .FirstOrDefaultAsyncLinqToDB(x => x.UserId == userId
                                              && x.ItemType == itemType
                                              && x.IsUsing);
    }

    public async Task<bool> UseShopItemAsync(ulong userId, XpShopItemType itemType, string key)
    {
        var data = _xpConfig.Data;
        XpConfig.ShopItemInfo? item = null;
        if (itemType == XpShopItemType.Background)
        {
            data.Shop.Bgs?.TryGetValue(key, out item);
        }
        else
        {
            data.Shop.Frames?.TryGetValue(key, out item);
        }

        if (item is null)
            return false;

        await using var ctx = _db.GetDbContext();

        if (await OwnsItemAsync(userId, itemType, key))
        {
            await ctx.GetTable<XpShopOwnedItem>()
                .Where(x => x.UserId == userId && x.ItemType == itemType)
                .UpdateAsync(old => new()
                {
                    IsUsing = key == old.ItemKey
                });

            return true;
        }

        return false;
    }

    public bool IsShopEnabled()
        => _xpConfig.Data.Shop.IsEnabled;

    public async Task<int> GetGuildXpUsersCountAsync(ulong requestGuildId, List<ulong>? guildUsers = null)
    {
        await using var ctx = _db.GetDbContext();
        return await ctx.GetTable<UserXpStats>()
            .Where(x => x.GuildId == requestGuildId
                        && (guildUsers == null || guildUsers.Contains(x.UserId)))
            .CountAsyncLinqToDB();
    }

    public async Task SetLevelAsync(ulong guildId, ulong userId, int level)
    {
        var lvlStats = LevelStats.CreateForLevel(level);
        await using var ctx = _db.GetDbContext();
        await ctx.GetTable<UserXpStats>()
            .InsertOrUpdateAsync(() => new()
                {
                    GuildId = guildId,
                    UserId = userId,
                    Xp = lvlStats.TotalXp,
                    DateAdded = DateTime.UtcNow
                },
                (old) => new()
                {
                    Xp = lvlStats.TotalXp
                },
                () => new()
                {
                    GuildId = guildId,
                    UserId = userId
                });
    }
}

public readonly record struct XpQueueEntry(IGuildUser User, long Xp, ulong? ChannelId)
{
    public bool Equals(XpQueueEntry? other)
        => other?.User == User;

    public override int GetHashCode()
        => User.GetHashCode();
}