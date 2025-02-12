﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web;

namespace RBX_Alt_Manager.Classes
{
    internal class Batch
    {
        /// <summary>
        /// Combines multiple requests into one using Roblox's API
        /// <para>
        /// The point of this class is to combine multiple requests into 1 request
        /// using Roblox's "multiget-place-details" and the thumbnail batch API by
        /// opening a small 50 millisecond timeframe to request multiple thumbnails
        /// for multiple games in separate code sections.
        /// </para>
        /// </summary>

        private static readonly RestClient ThumbnailAPI = new RestClient("https://thumbnails.roblox.com/");
        private static readonly RestClient GamesAPI = new RestClient("https://games.roblox.com/");

        private static readonly object PlaceTaskLock = new object();
        private static readonly object BatchTaskLock = new object();

        private static Task<JArray> CurrentBatchTask;
        private static Task CurrentPlaceTask;

        private static readonly List<object> PendingBatch = new List<object>();
        private static readonly List<long> PendingPlace = new List<long>();

        private static readonly Dictionary<long, long> PlaceUniversePair = new Dictionary<long, long>();
        public static readonly Dictionary<long, GameDetails> PlaceDetails = new Dictionary<long, GameDetails>();

        /// <summary>
        /// Get an image for the specified Asset
        /// </summary>
        /// <param name="AssetId">Asset ID</param>
        /// <param name="Type">Image Type (Avatar, AvatarHeadShot, GameIcon, BadgeIcon, GameThumbnail, GamePass, Asset, BundleThumbnail, Outfit, GroupIcon, DeveloperProduct, AutoGeneratedAsset, AvatarBust, PlaceIcon, AutoGeneratedGameIcon, ForceAutoGeneratedGameIcon)</param>
        /// <param name="Size">Image Size</param>
        /// <param name="Format">Image Format (png/jpeg)</param>
        /// <returns>A string containing the Image Url</returns>
        public static async Task<string> GetImage(long AssetId, string Type, string Size = "150x150", string Format = null)
        {
            if (AccountManager.General.Get<bool>("DisableImages"))
                return string.Empty;

            lock (BatchTaskLock)
            {
                if (CurrentBatchTask == null || CurrentBatchTask.IsCompleted || CurrentBatchTask.IsCanceled)
                {
                    CurrentBatchTask?.Dispose();
                    CurrentBatchTask = Task.Run(DoBatchRequest);
                }
            }

            PendingBatch.Add(new
            {
                requestId = $"{AssetId}:undefined:{Type}:{Size}:{Format}:regular",
                type = Type,
                targetId = AssetId,
                size = Size,
                format = Format ?? null
            });

            return await CurrentBatchTask.ContinueWith(task =>
            {
                if (CurrentBatchTask.IsFaulted)
                    throw CurrentBatchTask.Exception;

                return task.Result?.Where(x => x["errorCode"].Value<int>() == 0 && (x["targetId"]?.Value<long>() ?? 0) == AssetId).FirstOrDefault()?["imageUrl"]?.Value<string>() ?? "UNK";
            });
        }

        /// <summary>
        /// Get a game's icon
        /// </summary>
        /// <param name="PlaceId">The PlaceId of the target game</param>
        /// <returns></returns>
        public static async Task<string> GetGameIcon(long PlaceId)
        {
            if (AccountManager.General.Get<bool>("DisableImages"))
                return string.Empty;

            lock (PlaceTaskLock)
            {
                if (CurrentPlaceTask == null || CurrentPlaceTask.IsCompleted || CurrentPlaceTask.IsCanceled)
                {
                    CurrentPlaceTask?.Dispose();
                    CurrentPlaceTask = Task.Run(DoPlaceRequest);
                }
            }

            PendingPlace.Add(PlaceId);

            await CurrentPlaceTask;

            if (CurrentPlaceTask.IsFaulted)
                throw CurrentPlaceTask.Exception;

            if (!PlaceUniversePair.TryGetValue(PlaceId, out long UniverseId)) return await GetAssetImage(PlaceId);

            return await GetImage(UniverseId, "GameIcon", "512x512", "png");
        }

        private static async Task<JArray> DoBatchRequest()
        {
            await Task.Delay(50);

            JArray ReturnArray = new JArray();

            while (PendingBatch.Count > 0)
            {
                List<object> Pending = new List<object>();

                Pending.AddRange(PendingBatch.GetRange(0, Math.Min(PendingBatch.Count, 100)));
                PendingBatch.RemoveRange(0, Math.Min(PendingBatch.Count, 100));

                var Request = new RestRequest("v1/batch", Method.POST);
                Request.AddJsonBody(Pending.ToArray());

                var Response = await ThumbnailAPI.ExecuteAsync(Request);

                if (!Response.IsSuccessful)
                    throw new HttpException($"{Response.StatusCode} Batch request failed\nBody: {Request.Body.Value}\nError: {Response.ErrorMessage}");

                JArray Data = JObject.Parse(Response.Content)?["data"]?.Value<JArray>();

                if (Data != null)
                    foreach (JToken Token in Data)
                        ReturnArray.Add(Token);
            }

            return ReturnArray;
        }

        private static async Task DoPlaceRequest()
        {
            await Task.Delay(50);

            while (AccountManager.LastValidAccount == null)
                await Task.Delay(80);

            while (PendingPlace.Count > 0)
            {
                List<long> Pending = new List<long>();

                Pending.AddRange(PendingPlace.GetRange(0, Math.Min(PendingPlace.Count, 50)));
                PendingPlace.RemoveRange(0, Math.Min(PendingPlace.Count, 50));

                foreach (long PlaceId in new List<long>(Pending))
                    if (PlaceDetails.ContainsKey(PlaceId))
                        Pending.Remove(PlaceId);

                if (Pending.Count == 0) continue;

                var Request = new RestRequest($"v1/games/multiget-place-details?placeIds={string.Join("&placeIds=", Pending.ToArray())}");

                Request.AddCookie(".ROBLOSECURITY", AccountManager.LastValidAccount?.SecurityToken);

                IRestResponse DetailsResponse = await GamesAPI.ExecuteAsync(Request);

                if (DetailsResponse.IsSuccessful)
                {
                    GameDetails[] Games = JsonConvert.DeserializeObject<GameDetails[]>(DetailsResponse.Content); // JArray Places = JArray.Parse(DetailsResponse.Content);

                    foreach (GameDetails game in Games)
                    {
                        long PlaceId = game.placeId; // ["placeId"].Value<long>();

                        if (!PlaceDetails.ContainsKey(PlaceId))
                            PlaceDetails.Add(PlaceId, game);

                        if (!PlaceUniversePair.ContainsKey(PlaceId))
                            PlaceUniversePair.Add(PlaceId, game.universeId); // game["universeId"].Value<long
                    }
                }
            }
        }

        private static async Task<string> GetAssetImage(long AssetId) // bad fallback method
        {
            if (AccountManager.General.Get<bool>("DisableImages"))
                return string.Empty;

            var Request = new RestRequest($"v1/assets?assetIds={AssetId}&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false");

            Request.AddCookie(".ROBLOSECURITY", AccountManager.LastValidAccount?.SecurityToken);

            var Response = await ThumbnailAPI.ExecuteAsync(Request);

            if (!Response.IsSuccessful) {
                Program.Logger.Error($"{Response.StatusCode} Asset Image request failed\nError: {Response.ErrorMessage}\nContent: {Response.Content}");
                return "UNK"; // throw new HttpException($"{Response.StatusCode} Asset Image request failed\nError: {Response.ErrorMessage}");
                              }

            JArray Data = JObject.Parse(Response.Content)?["data"]?.Value<JArray>();

            if (Data != null && Data.Count > 0)
                return Data[0]?["imageUrl"]?.Value<string>();

            return "UNK";
        }
    }
}