﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bangumi.Configuration;
using Jellyfin.Plugin.Bangumi.Model;
using Jellyfin.Plugin.Bangumi.OAuth;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;
using CollectionType = Jellyfin.Plugin.Bangumi.Model.CollectionType;

namespace Jellyfin.Plugin.Bangumi;

public class PlaybackScrobbler : IServerEntryPoint
{
    // https://github.com/jellyfin/jellyfin/blob/master/Emby.Server.Implementations/Localization/Ratings/jp.csv
    // https://github.com/jellyfin/jellyfin/blob/master/Emby.Server.Implementations/Localization/Ratings/us.csv
    private const int RatingNSFW = 10;

    private static readonly Dictionary<Guid, HashSet<string>> Store = new();
    private readonly BangumiApi _api;
    private readonly ILocalizationManager _localizationManager;
    private readonly ILogger<PlaybackScrobbler> _log;

    private readonly OAuthStore _store;
    private readonly IUserDataManager _userDataManager;

    public PlaybackScrobbler(IUserManager userManager, IUserDataManager userDataManager, ILocalizationManager localizationManager, OAuthStore store, BangumiApi api, ILogger<PlaybackScrobbler> log)
    {
        _userDataManager = userDataManager;
        _localizationManager = localizationManager;
        _store = store;
        _api = api;
        _log = log;

        foreach (var userId in userManager.UsersIds)
            GetPlaybackHistory(userId);
    }

    private static PluginConfiguration Configuration => Plugin.Instance!.Configuration;

    public void Dispose()
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        GC.SuppressFinalize(this);
    }

    public Task RunAsync()
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        switch (e.SaveReason)
        {
            case UserDataSaveReason.TogglePlayed when e.UserData.Played:
                // delay 3 seconds to avoid conflict with playback finished event
                Task.Delay(TimeSpan.FromSeconds(3))
                    .ContinueWith(_ =>
                    {
                        GetPlaybackHistory(e.UserId).Add(e.UserData.Key);
                        _log.LogInformation("mark {Name} (#{Id}) as played for user #{User}", e.Item.Name, e.Item.Id, e.UserId);
                    }).ConfigureAwait(false);
                if (Configuration.ReportManualStatusChangeToBangumi)
                    ReportPlaybackStatus(e.Item, e.UserId, true).ConfigureAwait(false);
                break;

            case UserDataSaveReason.TogglePlayed when !e.UserData.Played:
                GetPlaybackHistory(e.UserId).Remove(e.UserData.Key);
                _log.LogInformation("mark {Name} (#{Id}) as new for user #{User}", e.Item.Name, e.Item.Id, e.UserId);
                if (Configuration.ReportManualStatusChangeToBangumi)
                    ReportPlaybackStatus(e.Item, e.UserId, true).ConfigureAwait(false);
                break;

            case UserDataSaveReason.PlaybackFinished when e.UserData.Played:
                if (Configuration.ReportPlaybackStatusToBangumi)
                    ReportPlaybackStatus(e.Item, e.UserId, true).ConfigureAwait(false);
                e.Keys.ForEach(key => GetPlaybackHistory(e.UserId).Add(key));
                break;
        }
    }

    private async Task ReportPlaybackStatus(BaseItem item, Guid userId, bool played)
    {
        if (!int.TryParse(item.GetProviderId(Constants.ProviderName), out var episodeId))
        {
            _log.LogInformation("item {Name} (#{Id}) doesn't have bangumi id, ignored", item.Name, item.Id);
            return;
        }

        if (!int.TryParse(item.GetParent()?.GetProviderId(Constants.ProviderName), out var subjectId))
            _log.LogWarning("parent of item {Name} (#{Id}) doesn't have bangumi subject id", item.Name, item.Id);

        if (item is Audio)
        {
            _log.LogInformation("audio playback report is not supported by bgm.tv, ignored");
            return;
        }

        if (item is Movie)
        {
            episodeId = subjectId;
            // jellyfin only have subject id for movie, so we need to get episode id from bangumi api
            var episodeList = await _api.GetSubjectEpisodeListWithOffset(subjectId, EpisodeType.Normal, 0, CancellationToken.None);
            if (episodeList?.Data.Count > 0)
                episodeId = episodeList.Data.First().Id;
        }

        var user = _store.Get(userId);
        if (user == null)
        {
            _log.LogInformation("access token for user #{User} not found, ignored", userId);
            return;
        }

        if (user.Expired)
        {
            _log.LogInformation("access token for user #{User} expired, ignored", userId);
            return;
        }

        if (item.GetUserDataKeys().Intersect(GetPlaybackHistory(userId)).Any())
        {
            _log.LogInformation("item {Name} (#{Id}) has been played before, ignored", item.Name, item.Id);
            return;
        }

        try
        {
            if (item is Book)
            {
                _log.LogInformation("report subject #{Subject} status {Status} to bangumi", episodeId, CollectionType.Watched);
                await _api.UpdateCollectionStatus(user.AccessToken, episodeId, played ? CollectionType.Watched : CollectionType.Watching, CancellationToken.None);
            }
            else
            {
                if (subjectId == 0)
                {
                    var episode = await _api.GetEpisode(episodeId, CancellationToken.None);
                    if (episode != null)
                        subjectId = episode.ParentId;
                }

                var ratingLevel = item.OfficialRating is null ? null : _localizationManager.GetRatingLevel(item.OfficialRating);
                if (ratingLevel == null)
                    foreach (var parent in item.GetParents())
                    {
                        if (parent.OfficialRating == null) continue;
                        ratingLevel = _localizationManager.GetRatingLevel(parent.OfficialRating);
                        if (ratingLevel != null) break;
                    }

                if (ratingLevel >= RatingNSFW && Configuration.SkipNSFWPlaybackReport)
                {
                    _log.LogInformation("item #{Name} marked as NSFW, skipped", item.Name);
                    return;
                }

                _log.LogInformation("report episode #{Episode} status {Status} to bangumi", episodeId, EpisodeCollectionType.Watched);
                await _api.UpdateEpisodeStatus(user.AccessToken, subjectId, episodeId, played ? EpisodeCollectionType.Watched : EpisodeCollectionType.Default, CancellationToken.None);
            }

            _log.LogInformation("report completed");
        }
        catch (Exception e)
        {
            _log.LogError(e, "report playback status failed");
        }
    }

    private HashSet<string> GetPlaybackHistory(Guid userId)
    {
        if (!Store.TryGetValue(userId, out var history))
            Store[userId] = history = _userDataManager.GetAllUserData(userId).Where(item => item.Played).Select(item => item.Key).ToHashSet();
        return history;
    }
}