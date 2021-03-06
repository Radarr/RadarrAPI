﻿using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using RadarrAPI.Database;
using RadarrAPI.Database.Models;
using RadarrAPI.Services.ReleaseCheck.Azure.Responses;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using RadarrAPI.Options;
using OperatingSystem = RadarrAPI.Update.OperatingSystem;
using RadarrAPI.Update;

namespace RadarrAPI.Services.ReleaseCheck.Azure
{
    public class AzureReleaseSource : ReleaseSourceBase
    {
        private const string AccountName = "Radarr";
        private const string ProjectSlug = "Radarr";
        private const string BranchName = "aphrodite";
        private const string PackageArtifactName = "Packages";

        private static int? _lastBuildId;

        private readonly DatabaseContext _database;
        private readonly RadarrOptions _config;
        private readonly HttpClient _httpClient;

        public AzureReleaseSource(DatabaseContext database, IHttpClientFactory httpClientFactory, IOptions<RadarrOptions> config)
        {
            _database = database;
            _config = config.Value;
            _httpClient = httpClientFactory.CreateClient();
        }

        protected override async Task<bool> DoFetchReleasesAsync()
        {
            if (ReleaseBranch == Branch.Unknown)
            {
                throw new ArgumentException("ReleaseBranch must not be unknown when fetching releases.");
            }

            var hasNewRelease = false;
            var historyUrl = $"https://dev.azure.com/{AccountName}/{ProjectSlug}/_apis/build/builds?api-version=5.1&branchName=refs/heads/{BranchName}&reasonFilter=individualCI&statusFilter=completed&resultFilter=succeeded&queryOrder=startTimeDescending&$top=5";
            var historyData = await _httpClient.GetStringAsync(historyUrl);
            var history = JsonConvert.DeserializeObject<AzureList<AzureProjectBuild>>(historyData).Value;

            // Store here temporarily so we don't break on not processed builds.
            var lastBuild = _lastBuildId;

            // URL query has filtered to most recent 5 successful, completed builds
            foreach (var build in history)
            {
                if (lastBuild.HasValue && lastBuild.Value >= build.BuildId)
                {
                    break;
                }

                // Found a build that hasn't started yet..?
                if (!build.Started.HasValue)
                {
                    break;
                }

                // Get build changes
                var changesPath = $"https://dev.azure.com/{AccountName}/{ProjectSlug}/_apis/build/builds/{build.BuildId}/changes?api-version=5.1";
                var changesData = await _httpClient.GetStringAsync(changesPath);
                var changes = JsonConvert.DeserializeObject<AzureList<AzureChange>>(changesData).Value;

                // Grab artifacts
                var artifactsPath = $"https://dev.azure.com/{AccountName}/{ProjectSlug}/_apis/build/builds/{build.BuildId}/artifacts?api-version=5.1";
                var artifactsData = await _httpClient.GetStringAsync(artifactsPath);
                var artifacts = JsonConvert.DeserializeObject<AzureList<AzureArtifact>>(artifactsData).Value;

                // there should be a single artifact called 'Packages' we parse for packages
                var artifact = artifacts.FirstOrDefault(x => x.Name == PackageArtifactName);
                if (artifact == null)
                {
                    continue;
                }

                // Download the manifest
                var manifestPath = $"https://dev.azure.com/{AccountName}/{ProjectSlug}/_apis/build/builds/{build.BuildId}/artifacts?artifactName={artifact.Name}&fileId={artifact.Resource.Data}&fileName=manifest&api-version=5.1";
                var manifestData = await _httpClient.GetStringAsync(manifestPath);
                var files = JsonConvert.DeserializeObject<AzureManifest>(manifestData).Files;

                // Get an updateEntity
                var updateEntity = _database.UpdateEntities
                    .Include(x => x.UpdateFiles)
                    .FirstOrDefault(x => x.Version.Equals(build.Version) && x.Branch.Equals(ReleaseBranch));

                if (updateEntity == null)
                {
                    // Create update object
                    updateEntity = new UpdateEntity
                    {
                        Version = build.Version,
                        ReleaseDate = build.Started.Value.UtcDateTime,
                        Branch = ReleaseBranch,
                        New = changes.Select(x => x.Message).ToList()
                    };

                    // Start tracking this object
                    await _database.AddAsync(updateEntity);

                    // Set new release to true.
                    hasNewRelease = true;
                }

                // Process artifacts
                foreach (var file in files)
                {
                    // Detect target operating system.
                    OperatingSystem operatingSystem;

                    if (file.Path.Contains("windows.") && file.Path.ToLower().Contains(".zip"))
                    {
                        operatingSystem = OperatingSystem.Windows;
                    }
                    else if (file.Path.Contains("linux."))
                    {
                        operatingSystem = OperatingSystem.Linux;
                    }
                    else if (file.Path.Contains("osx."))
                    {
                        operatingSystem = OperatingSystem.Osx;
                    }
                    else
                    {
                        continue;
                    }

                    // Check if exists in database.
                    var updateFileEntity = _database.UpdateFileEntities
                        .FirstOrDefault(x =>
                            x.UpdateEntityId == updateEntity.UpdateEntityId &&
                            x.OperatingSystem == operatingSystem);

                    if (updateFileEntity != null) continue;

                    // Calculate the hash of the zip file.
                    var releaseFileName = Path.GetFileName(file.Path);
                    var releaseDownloadUrl = $"https://dev.azure.com/{AccountName}/{ProjectSlug}/_apis/build/builds/{build.BuildId}/artifacts?artifactName={artifact.Name}&fileId={file.Blob.Id}&fileName={releaseFileName}&api-version=5.1";
                    var releaseZip = Path.Combine(_config.DataDirectory, ReleaseBranch.ToString(), releaseFileName);
                    string releaseHash;

                    if (!File.Exists(releaseZip))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(releaseZip));

                        using (var fileStream = File.OpenWrite(releaseZip))
                        using (var artifactStream = await _httpClient.GetStreamAsync(releaseDownloadUrl))
                        {
                            await artifactStream.CopyToAsync(fileStream);
                        }
                    }

                    using (var stream = File.OpenRead(releaseZip))
                    {
                        using (var sha = SHA256.Create())
                        {
                            releaseHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLower();
                        }
                    }

                    File.Delete(releaseZip);

                    // Add to database.
                    updateEntity.UpdateFiles.Add(new UpdateFileEntity
                    {
                        OperatingSystem = operatingSystem,
                        Filename = releaseFileName,
                        Url = releaseDownloadUrl,
                        Hash = releaseHash
                    });
                }

                // Save all changes to the database.
                await _database.SaveChangesAsync();

                // Make sure we atleast skip this build next time.
                if (_lastBuildId == null ||
                    _lastBuildId.Value < build.BuildId)
                {
                    _lastBuildId = build.BuildId;
                }
            }

            return hasNewRelease;
        }
    }
}
