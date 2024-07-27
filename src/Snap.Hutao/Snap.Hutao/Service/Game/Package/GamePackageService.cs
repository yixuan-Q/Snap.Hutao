﻿// Copyright (c) DGP Studio. All rights reserved.
// Licensed under the MIT license.

using CommunityToolkit.Common;
using Microsoft.Win32.SafeHandles;
using Snap.Hutao.Core.DependencyInjection.Abstraction;
using Snap.Hutao.Core.IO;
using Snap.Hutao.Core.IO.Compression.Zstandard;
using Snap.Hutao.Core.IO.Hashing;
using Snap.Hutao.Factory.ContentDialog;
using Snap.Hutao.Factory.IO;
using Snap.Hutao.Factory.Progress;
using Snap.Hutao.Service.Game.Scheme;
using Snap.Hutao.UI.Xaml.View.Window;
using Snap.Hutao.ViewModel.Game;
using Snap.Hutao.Web.Hoyolab.Takumi.Downloader;
using Snap.Hutao.Web.Hoyolab.Takumi.Downloader.Proto;
using Snap.Hutao.Web.Response;
using System.Buffers;
using System.IO;
using System.Net.Http;

namespace Snap.Hutao.Service.Game.Package;

[ConstructorGenerated]
[Injection(InjectAs.Singleton, typeof(IGamePackageService))]
[SuppressMessage("", "CA1001")]
[SuppressMessage("", "SA1204")]
internal sealed partial class GamePackageService : IGamePackageService
{
    private readonly JsonSerializerOptions jsonSerializerOptions;
    private readonly IMemoryStreamFactory memoryStreamFactory;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly IContentDialogFactory contentDialogFactory;
    private readonly IProgressFactory progressFactory;
    private readonly IServiceProvider serviceProvider;
    private readonly LaunchOptions launchOptions;
    private readonly ITaskContext taskContext;

    private CancellationTokenSource? operationCts;
    private TaskCompletionSource? operationTcs;

    public async ValueTask<bool> StartOperationAsync(GamePackageOperationContext context)
    {
        await CancelOperationAsync().ConfigureAwait(true);

        GamePackageOperationWindow window = serviceProvider.GetRequiredService<GamePackageOperationWindow>();

        operationCts = new();
        operationTcs = new();
        ParallelOptions parallelOptions = new()
        {
            CancellationToken = operationCts.Token,
            MaxDegreeOfParallelism = Environment.ProcessorCount,
        };

        ValueTask operation = context.State switch
        {
            GamePackageOperationState.Install => InstallAsync(window, context, parallelOptions, operationCts.Token),
            GamePackageOperationState.Verify => VerifyAsync(window, context, parallelOptions, operationCts.Token),
            GamePackageOperationState.Update => UpdateAsync(window, context, parallelOptions, operationCts.Token),
            GamePackageOperationState.Predownload => PredownloadAsync(window, context, parallelOptions, operationCts.Token),
            _ => ValueTask.CompletedTask,
        };

        try
        {
            await operation.ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            operationTcs.TrySetResult();
        }
    }

    public async ValueTask CancelOperationAsync()
    {
        if (operationCts is null || operationTcs is null)
        {
            return;
        }

        await operationCts.CancelAsync().ConfigureAwait(false);
        await operationTcs.Task.ConfigureAwait(false);
        operationCts.Dispose();
        operationCts = null;
        operationTcs = null;
    }

    #region Operation

    private async ValueTask InstallAsync(GamePackageOperationWindow window, GamePackageOperationContext context, ParallelOptions parallelOptions, CancellationToken token = default)
    {
        GamePackageOperationViewModel viewModel = (GamePackageOperationViewModel)window.DataContext;

        IProgress<GamePackageOperationDownloadStatus> progress = progressFactory.CreateForMainThread<GamePackageOperationDownloadStatus>(viewModel.UpdateProgress);

        Response<SophonBuild> response;
        using (IServiceScope scope = serviceProvider.CreateScope())
        {
            ISophonClient client = scope.ServiceProvider
                .GetRequiredService<IOverseaSupportFactory<ISophonClient>>()
                .Create(LaunchScheme.ExecutableIsOversea(context.GameFileSystem.GameFileName));

            response = await client.GetBuildAsync(context.RemoteBranch, token).ConfigureAwait(false);
        }

        if (!response.IsOk())
        {
            window.DispatcherQueue.TryEnqueue(() => viewModel.ResetProgress("清单数据拉取失败", 0, 0));
            return;
        }

        SophonDecodedBuild sophonDecodedBuild = await DecodeManifestsAsync(response.Data, context, token).ConfigureAwait(false);

        long totalBytes = sophonDecodedBuild.TotalBytes;
        long availableBytes = GetDiskAvailableFreeSpace(context.GameFileSystem);

        if (totalBytes > availableBytes)
        {
            window.DispatcherQueue.TryEnqueue(() => viewModel.ResetProgress($"磁盘空间不足，需要 {Converters.ToFileSizeString(totalBytes)}，剩余 {Converters.ToFileSizeString(availableBytes)}", 0, 0));
            return;
        }

        window.DispatcherQueue.TryEnqueue(() => viewModel.ResetProgress("正在安装游戏", sophonDecodedBuild.Manifests.Sum(m => m.ManifestProto.Assets.Sum(a => a.AssetChunks.Count)), sophonDecodedBuild.TotalBytes));

        foreach (SophonDecodedManifest sophonDecodedManifest in sophonDecodedBuild.Manifests)
        {
            await Parallel.ForEachAsync(sophonDecodedManifest.ManifestProto.Assets, parallelOptions, async (asset, token) =>
            {
                await DownloadAssetChunksAsync(new(sophonDecodedManifest.UrlPrefix, asset), context, progress, parallelOptions, token).ConfigureAwait(false);
                await MergeAssetAsync(asset, context, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        // Verify
        window.DispatcherQueue.TryEnqueue(() => viewModel.ResetProgress("正在验证游戏完整性", sophonDecodedBuild.Manifests.Sum(m => m.ManifestProto.Assets.Sum(a => a.AssetChunks.Count)), sophonDecodedBuild.TotalBytes));

        List<SophonAsset> conflictAssets = [];

        foreach (SophonDecodedManifest sophonDecodedManifest in sophonDecodedBuild.Manifests)
        {
            await Parallel.ForEachAsync(sophonDecodedManifest.ManifestProto.Assets, parallelOptions, (asset, token) => VerifyAssetAsync(new(sophonDecodedManifest.UrlPrefix, asset), conflictAssets, context, progress, token)).ConfigureAwait(false);
        }

        if (conflictAssets.IsNullOrEmpty())
        {
            window.DispatcherQueue.TryEnqueue(() => viewModel.FinishOperation(context.State));
            Directory.Delete(context.GameFileSystem.ChunksDirectory, true);
            return;
        }

        window.DispatcherQueue.TryEnqueue(() => viewModel.ResetProgress("正在修复游戏完整性", conflictAssets.Sum(a => a.AssetProperty.AssetChunks.Count), conflictAssets.Sum(a => a.AssetProperty.AssetSize)));

        await Parallel.ForEachAsync(conflictAssets, parallelOptions, async (asset, token) =>
        {
            await DownloadAssetChunksAsync(asset, context, progress, parallelOptions, token).ConfigureAwait(false);
            await MergeAssetAsync(asset.AssetProperty, context, token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        window.DispatcherQueue.TryEnqueue(() => viewModel.FinishOperation(context.State));
        Directory.Delete(context.GameFileSystem.ChunksDirectory, true);
    }

    private async ValueTask VerifyAsync(GamePackageOperationWindow window, GamePackageOperationContext context, ParallelOptions parallelOptions, CancellationToken token = default)
    {
        GamePackageOperationViewModel viewModel = (GamePackageOperationViewModel)window.DataContext;

        IProgress<GamePackageOperationDownloadStatus> progress = progressFactory.CreateForMainThread<GamePackageOperationDownloadStatus>(viewModel.UpdateProgress);

        Response<SophonBuild> sophonBuildResp;
        using (IServiceScope scope = serviceProvider.CreateScope())
        {
            ISophonClient client = scope.ServiceProvider
                .GetRequiredService<IOverseaSupportFactory<ISophonClient>>()
                .Create(LaunchScheme.ExecutableIsOversea(context.GameFileSystem.GameFileName));

            sophonBuildResp = await client.GetBuildAsync(context.LocalBranch, token).ConfigureAwait(false);
        }

        if (!sophonBuildResp.IsOk())
        {
            window.DispatcherQueue.TryEnqueue(() => viewModel.ResetProgress("清单数据拉取失败", 0, 0));
            return;
        }

        SophonDecodedBuild sophonDecodedBuild = await DecodeManifestsAsync(sophonBuildResp.Data, context, token).ConfigureAwait(false);

        window.DispatcherQueue.TryEnqueue(() => viewModel.ResetProgress("正在验证游戏完整性", sophonDecodedBuild.Manifests.Sum(m => m.ManifestProto.Assets.Sum(a => a.AssetChunks.Count)), sophonDecodedBuild.TotalBytes));

        List<SophonAsset> conflictAssets = [];

        foreach (SophonDecodedManifest sophonDecodedManifest in sophonDecodedBuild.Manifests)
        {
            await Parallel.ForEachAsync(sophonDecodedManifest.ManifestProto.Assets, parallelOptions, (asset, token) => VerifyAssetAsync(new(sophonDecodedManifest.UrlPrefix, asset), conflictAssets, context, progress, token)).ConfigureAwait(false);
        }

        if (conflictAssets.IsNullOrEmpty())
        {
            window.DispatcherQueue.TryEnqueue(() => viewModel.FinishOperation(context.State, false));
            return;
        }

        window.DispatcherQueue.TryEnqueue(() => viewModel.ResetProgress("正在修复游戏完整性", conflictAssets.Sum(a => a.AssetProperty.AssetChunks.Count), conflictAssets.Sum(a => a.AssetProperty.AssetSize)));

        await Parallel.ForEachAsync(conflictAssets, parallelOptions, async (asset, token) =>
        {
            await DownloadAssetChunksAsync(asset, context, progress, parallelOptions, token).ConfigureAwait(false);
            await MergeAssetAsync(asset.AssetProperty, context, token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        window.DispatcherQueue.TryEnqueue(() => viewModel.FinishOperation(context.State, true));
        Directory.Delete(context.GameFileSystem.ChunksDirectory, true);
    }

    private async ValueTask UpdateAsync(GamePackageOperationWindow window, GamePackageOperationContext context, ParallelOptions parallelOptions, CancellationToken token = default)
    {
        GamePackageOperationViewModel viewModel = (GamePackageOperationViewModel)window.DataContext;

        IProgress<GamePackageOperationDownloadStatus> progress = progressFactory.CreateForMainThread<GamePackageOperationDownloadStatus>(viewModel.UpdateProgress);

        SophonBuild localBuild;
        SophonBuild remoteBuild;

        using (IServiceScope scope = serviceProvider.CreateScope())
        {
            ISophonClient client = scope.ServiceProvider.GetRequiredService<IOverseaSupportFactory<ISophonClient>>()
                .Create(LaunchScheme.ExecutableIsOversea(context.GameFileSystem.GameFileName));

            Response<SophonBuild> sophonBuildResp = await client.GetBuildAsync(context.LocalBranch, token).ConfigureAwait(false);
            if (!sophonBuildResp.IsOk())
            {
                window.DispatcherQueue.TryEnqueue(() => viewModel.ResetProgress("清单数据拉取失败", 0, 0));
                return;
            }

            localBuild = sophonBuildResp.Data;

            sophonBuildResp = await client.GetBuildAsync(context.RemoteBranch, token).ConfigureAwait(false);
            if (!sophonBuildResp.IsOk())
            {
                window.DispatcherQueue.TryEnqueue(() => viewModel.ResetProgress("清单数据拉取失败", 0, 0));
                return;
            }

            remoteBuild = sophonBuildResp.Data;
        }

        SophonDecodedBuild localDecodedBuild = await DecodeManifestsAsync(localBuild, context, token).ConfigureAwait(false);
        SophonDecodedBuild remoteDecodedBuild = await DecodeManifestsAsync(remoteBuild, context, token).ConfigureAwait(false);

        ParseDiff(localDecodedBuild, remoteDecodedBuild, out List<SophonAsset> addedAssets, out Dictionary<AssetProperty, SophonAsset> modifiedAssets, out List<AssetProperty> deletedAssets);

        // Patch
        int totalBlocks = addedAssets.Sum(a => a.AssetProperty.AssetChunks.Count) + modifiedAssets.Sum(a => a.Value.DiffChunks.Count);
        long totalBytes = addedAssets.Sum(a => a.AssetProperty.AssetSize) + modifiedAssets.Sum(a => a.Value.DiffChunks.Sum(c => c.AssetChunk.ChunkSizeDecompressed));

        long availableBytes = GetDiskAvailableFreeSpace(context.GameFileSystem);

        if (totalBytes > availableBytes)
        {
            window.DispatcherQueue.TryEnqueue(() => viewModel.ResetProgress($"磁盘空间不足，需要 {Converters.ToFileSizeString(totalBytes)}，剩余 {Converters.ToFileSizeString(availableBytes)}", 0, 0));
            return;
        }

        window.DispatcherQueue.TryEnqueue(() => viewModel.ResetProgress("正在更新游戏", totalBlocks, totalBytes));

        // Added
        await Parallel.ForEachAsync(addedAssets, parallelOptions, async (asset, token) =>
        {
            if (asset.AssetProperty.AssetType is 64)
            {
                Directory.CreateDirectory(Path.Combine(context.GameFileSystem.GameDirectory, asset.AssetProperty.AssetName));
                return;
            }

            await DownloadAssetChunksAsync(asset, context, progress, parallelOptions, token).ConfigureAwait(false);
            await MergeAssetAsync(asset.AssetProperty, context, token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        // Modified
        // 内容未发生变化但是偏移量发生变化的块，从旧asset读取并写入新asset流
        // 内容发生变化的块直接读取diff chunk写入新asset流
        await Parallel.ForEachAsync(modifiedAssets, parallelOptions, async (asset, token) =>
        {
            foreach (SophonChunk sophonChunk in asset.Value.DiffChunks)
            {
                await DownloadChunkAsync(sophonChunk, context, progress, token).ConfigureAwait(false);
            }

            await MergeDiffAssetAsync(asset.Key, asset.Value, context, token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        // Deleted
        foreach (AssetProperty asset in deletedAssets)
        {
            string assetPath = Path.Combine(context.GameFileSystem.GameDirectory, asset.AssetName);

            if (asset.AssetType is 64)
            {
                Directory.Delete(assetPath, true);
            }

            if (File.Exists(assetPath))
            {
                File.Delete(assetPath);
            }
        }

        // Verify
        window.DispatcherQueue.TryEnqueue(() => viewModel.ResetProgress("正在验证游戏完整性", remoteDecodedBuild.Manifests.Sum(m => m.ManifestProto.Assets.Sum(a => a.AssetChunks.Count)), remoteDecodedBuild.TotalBytes));

        List<SophonAsset> conflictAssets = [];

        foreach (SophonDecodedManifest sophonDecodedManifest in remoteDecodedBuild.Manifests)
        {
            await Parallel.ForEachAsync(sophonDecodedManifest.ManifestProto.Assets, parallelOptions, (asset, token) => VerifyAssetAsync(new(sophonDecodedManifest.UrlPrefix, asset), conflictAssets, context, progress, token)).ConfigureAwait(false);
        }

        if (conflictAssets.IsNullOrEmpty())
        {
            window.DispatcherQueue.TryEnqueue(() => viewModel.FinishOperation(context.State));
            Directory.Delete(context.GameFileSystem.ChunksDirectory, true);
            return;
        }

        window.DispatcherQueue.TryEnqueue(() => viewModel.ResetProgress("正在修复游戏完整性", conflictAssets.Sum(a => a.AssetProperty.AssetChunks.Count), conflictAssets.Sum(a => a.AssetProperty.AssetSize)));

        await Parallel.ForEachAsync(conflictAssets, parallelOptions, async (asset, token) =>
        {
            await DownloadAssetChunksAsync(asset, context, progress, parallelOptions, token).ConfigureAwait(false);
            await MergeAssetAsync(asset.AssetProperty, context, token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        window.DispatcherQueue.TryEnqueue(() => viewModel.FinishOperation(context.State));
        Directory.Delete(context.GameFileSystem.ChunksDirectory, true);
    }

    private async ValueTask PredownloadAsync(GamePackageOperationWindow window, GamePackageOperationContext context, ParallelOptions parallelOptions, CancellationToken token = default)
    {
        GamePackageOperationViewModel viewModel = (GamePackageOperationViewModel)window.DataContext;

        IProgress<GamePackageOperationDownloadStatus> progress = progressFactory.CreateForMainThread<GamePackageOperationDownloadStatus>(viewModel.UpdateProgress);

        SophonBuild localBuild;
        SophonBuild remoteBuild;

        using (IServiceScope scope = serviceProvider.CreateScope())
        {
            ISophonClient client = scope.ServiceProvider.GetRequiredService<IOverseaSupportFactory<ISophonClient>>()
                .Create(LaunchScheme.ExecutableIsOversea(context.GameFileSystem.GameFileName));

            Response<SophonBuild> sophonBuildResp = await client.GetBuildAsync(context.LocalBranch, token).ConfigureAwait(false);
            if (!sophonBuildResp.IsOk())
            {
                window.DispatcherQueue.TryEnqueue(() => viewModel.ResetProgress("清单数据拉取失败", 0, 0));
                return;
            }

            localBuild = sophonBuildResp.Data;

            sophonBuildResp = await client.GetBuildAsync(context.RemoteBranch, token).ConfigureAwait(false);
            if (!sophonBuildResp.IsOk())
            {
                window.DispatcherQueue.TryEnqueue(() => viewModel.ResetProgress("清单数据拉取失败", 0, 0));
                return;
            }

            remoteBuild = sophonBuildResp.Data;
        }

        SophonDecodedBuild localDecodedBuild = await DecodeManifestsAsync(localBuild, context, token).ConfigureAwait(false);
        SophonDecodedBuild remoteDecodedBuild = await DecodeManifestsAsync(remoteBuild, context, token).ConfigureAwait(false);

        // 和Update相比不需要处理delete，不需要Combine
        ParseDiff(localDecodedBuild, remoteDecodedBuild, out List<SophonAsset> addedAssets, out Dictionary<AssetProperty, SophonAsset> modifiedAssets, out _);

        // Download
        int totalBlocks = addedAssets.Sum(a => a.AssetProperty.AssetChunks.Count) + modifiedAssets.Sum(a => a.Value.DiffChunks.Count);
        long totalBytes = addedAssets.Sum(a => a.AssetProperty.AssetSize) + modifiedAssets.Sum(a => a.Value.DiffChunks.Sum(c => c.AssetChunk.ChunkSizeDecompressed));

        long availableBytes = GetDiskAvailableFreeSpace(context.GameFileSystem);

        if (totalBytes > availableBytes)
        {
            window.DispatcherQueue.TryEnqueue(() => viewModel.ResetProgress($"磁盘空间不足，需要 {Converters.ToFileSizeString(totalBytes)}，剩余 {Converters.ToFileSizeString(availableBytes)}", 0, 0));
            return;
        }

        window.DispatcherQueue.TryEnqueue(() => viewModel.ResetProgress("正在预下载资源", totalBlocks, totalBytes));

        PredownloadStatus predownloadStatus = new(context.RemoteBranch.Tag, false, totalBlocks);
        using (FileStream predownloadStatusStream = File.Create(context.GameFileSystem.PredownloadStatusPath))
        {
            await JsonSerializer.SerializeAsync(predownloadStatusStream, predownloadStatus, jsonSerializerOptions, token).ConfigureAwait(false);
        }

        // Added
        await Parallel.ForEachAsync(addedAssets, parallelOptions, (asset, token) => DownloadAssetChunksAsync(asset, context, progress, parallelOptions, token)).ConfigureAwait(false);

        // Modified
        await Parallel.ForEachAsync(modifiedAssets, parallelOptions, async (asset, token) =>
        {
            foreach (SophonChunk sophonChunk in asset.Value.DiffChunks)
            {
                await DownloadChunkAsync(sophonChunk, context, progress, token).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        window.DispatcherQueue.TryEnqueue(() => viewModel.FinishOperation(context.State));

        using (FileStream predownloadStatusStream = File.Create(context.GameFileSystem.PredownloadStatusPath))
        {
            predownloadStatus.Finished = true;
            await JsonSerializer.SerializeAsync(predownloadStatusStream, predownloadStatus, jsonSerializerOptions, token).ConfigureAwait(false);
        }

        return;
    }

    #endregion

    #region Parse

    private async ValueTask<SophonDecodedBuild> DecodeManifestsAsync(SophonBuild sophonBuild, GamePackageOperationContext context, CancellationToken token = default)
    {
        long totalBytes = 0L;
        List<SophonDecodedManifest> decodedManifests = [];
        foreach (SophonManifest sophonManifest in sophonBuild.Manifests)
        {
            bool exclude = sophonManifest.MatchingField switch
            {
                "game" => false,
                "zh-cn" => !context.GameFileSystem.GameAudioSystem.Chinese,
                "en-us" => !context.GameFileSystem.GameAudioSystem.English,
                "ja-jp" => !context.GameFileSystem.GameAudioSystem.Japanese,
                "ko-kr" => !context.GameFileSystem.GameAudioSystem.Korean,
                _ => true,
            };

            if (exclude)
            {
                continue;
            }

            totalBytes += sophonManifest.Stats.UncompressedSize;
            string manifestDownloadUrl = $"{sophonManifest.ManifestDownload.UrlPrefix}/{sophonManifest.Manifest.Id}";

            using (HttpClient httpClient = httpClientFactory.CreateClient(nameof(GamePackageService)))
            {
                using (Stream rawManifestStream = await httpClient.GetStreamAsync(manifestDownloadUrl, token).ConfigureAwait(false))
                {
                    using (ZstandardDecompressionStream decompressor = new(rawManifestStream))
                    {
                        using (MemoryStream inMemoryManifestStream = await memoryStreamFactory.GetStreamAsync(decompressor).ConfigureAwait(false))
                        {
                            string manifestMd5 = await MD5.HashAsync(inMemoryManifestStream, token).ConfigureAwait(false);
                            if (manifestMd5.Equals(sophonManifest.Manifest.Checksum, StringComparison.OrdinalIgnoreCase))
                            {
                                inMemoryManifestStream.Position = 0;
                                decodedManifests.Add(new(sophonManifest.ChunkDownload.UrlPrefix, SophonManifestProto.Parser.ParseFrom(inMemoryManifestStream)));
                            }
                        }
                    }
                }
            }
        }

        return new(totalBytes, decodedManifests);
    }

    private static void ParseDiff(SophonDecodedBuild localDecodedBuild, SophonDecodedBuild remoteDecodedBuild, out List<SophonAsset> addedAssets, out Dictionary<AssetProperty, SophonAsset> modifiedAssets, out List<AssetProperty> deletedAssets)
    {
        addedAssets = [];
        modifiedAssets = [];
        deletedAssets = [];

        // Add
        // 本地没有，远端有
        foreach ((SophonDecodedManifest localManifest, SophonDecodedManifest remoteManifest) in localDecodedBuild.Manifests.Zip(remoteDecodedBuild.Manifests))
        {
            foreach (SophonAsset sophonAsset in remoteManifest.ManifestProto.Assets.Except(localManifest.ManifestProto.Assets, AssetPropertyNameComparer.Shared).Select(ap => new SophonAsset(remoteManifest.UrlPrefix, ap)))
            {
                addedAssets.Add(sophonAsset);
            }
        }

        // Modify
        // 本地有，远端有，但是内容不一致
        foreach ((SophonDecodedManifest localManifest, SophonDecodedManifest remoteManifest) in localDecodedBuild.Manifests.Zip(remoteDecodedBuild.Manifests))
        {
            foreach (AssetProperty asset in remoteManifest.ManifestProto.Assets)
            {
                AssetProperty? localAsset = localManifest.ManifestProto.Assets.FirstOrDefault(a => a.AssetName.Equals(asset.AssetName, StringComparison.OrdinalIgnoreCase));
                if (localAsset is null)
                {
                    continue;
                }

                if (localAsset.AssetHashMd5.Equals(asset.AssetHashMd5, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                modifiedAssets.Add(localAsset, new(remoteManifest.UrlPrefix, asset, asset.AssetChunks.Except(localAsset.AssetChunks, AssetChunkMd5Comparer.Shared).Select(ac => new SophonChunk(remoteManifest.UrlPrefix, ac)).ToList()));
            }
        }

        // Delete
        // 本地有，远端没有
        foreach ((SophonDecodedManifest localManifest, SophonDecodedManifest remoteManifest) in localDecodedBuild.Manifests.Zip(remoteDecodedBuild.Manifests))
        {
            localManifest.ManifestProto.Assets.Except(remoteManifest.ManifestProto.Assets, AssetPropertyNameComparer.Shared).ToList().ForEach(deletedAssets.Add);
        }
    }

    #endregion

    #region Asset Operation

    private async ValueTask VerifyAssetAsync(SophonAsset sophonAsset, List<SophonAsset> conflictAssets, GamePackageOperationContext context, IProgress<GamePackageOperationDownloadStatus> progress, CancellationToken token = default)
    {
        string assetPath = Path.Combine(context.GameFileSystem.GameDirectory, sophonAsset.AssetProperty.AssetName);

        if (sophonAsset.AssetProperty.AssetType is 64)
        {
            Directory.CreateDirectory(assetPath);
            return;
        }

        if (!File.Exists(assetPath))
        {
            conflictAssets.Add(sophonAsset);
            for (int i = 0; i < sophonAsset.AssetProperty.AssetChunks.Count; i++)
            {
                progress.Report(new(0, true));
            }

            return;
        }

        using (FileStream fileStream = File.OpenRead(assetPath))
        {
            for (int i = 0; i < sophonAsset.AssetProperty.AssetChunks.Count; i++)
            {
                AssetChunk chunk = sophonAsset.AssetProperty.AssetChunks[i];
                using (Stream hashStream = await fileStream.CloneSegmentAsync(chunk.ChunkOnFileOffset, chunk.ChunkSizeDecompressed, memoryStreamFactory).ConfigureAwait(false))
                {
                    string chunkMd5 = await MD5.HashAsync(hashStream, token).ConfigureAwait(false);
                    if (!chunkMd5.Equals(chunk.ChunkDecompressedHashMd5, StringComparison.OrdinalIgnoreCase))
                    {
                        conflictAssets.Add(sophonAsset);
                        for (int j = i; j < sophonAsset.AssetProperty.AssetChunks.Count; j++)
                        {
                            progress.Report(new(0, true));
                        }

                        return;
                    }

                    progress.Report(new(chunk.ChunkSizeDecompressed, true));
                }
            }
        }
    }

    private async ValueTask DownloadAssetChunksAsync(SophonAsset sophonAsset, GamePackageOperationContext context, IProgress<GamePackageOperationDownloadStatus> progress, ParallelOptions parallelOptions, CancellationToken token = default)
    {
        await Parallel.ForEachAsync(sophonAsset.AssetProperty.AssetChunks, parallelOptions, (chunk, token) => DownloadChunkAsync(new(sophonAsset.UrlPrefix, chunk), context, progress, token)).ConfigureAwait(false);
    }

    private async ValueTask DownloadChunkAsync(SophonChunk sophonChunk, GamePackageOperationContext context, IProgress<GamePackageOperationDownloadStatus> progress, CancellationToken token = default)
    {
        Directory.CreateDirectory(context.GameFileSystem.ChunksDirectory);
        string chunkPath = Path.Combine(context.GameFileSystem.ChunksDirectory, sophonChunk.AssetChunk.ChunkName);
        if (File.Exists(chunkPath))
        {
            string chunkXxh64 = await XXH64.HashFileAsync(chunkPath, token).ConfigureAwait(false);
            if (chunkXxh64.Equals(sophonChunk.AssetChunk.ChunkName.Split("_")[0], StringComparison.OrdinalIgnoreCase))
            {
                progress.Report(new(sophonChunk.AssetChunk.ChunkSize, true));
                return;
            }

            File.Delete(chunkPath);
        }

        using (FileStream fileStream = File.Create(chunkPath))
        {
            fileStream.Position = 0;

            using (HttpClient httpClient = httpClientFactory.CreateClient(nameof(GamePackageService)))
            {
                using (HttpResponseMessage responseMessage = await httpClient.GetAsync(sophonChunk.ChunkDownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    long totalBytes = responseMessage.Content.Headers.ContentLength ?? 0;
                    using (Stream webStream = await responseMessage.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                    {
                        StreamCopyWorker<GamePackageOperationDownloadStatus> worker = new(webStream, fileStream, bytesRead => new(bytesRead, false));

                        await worker.CopyAsync(progress).ConfigureAwait(false);

                        fileStream.Position = 0;
                        string chunkXxh64 = await XXH64.HashAsync(fileStream, token).ConfigureAwait(false);
                        if (chunkXxh64.Equals(sophonChunk.AssetChunk.ChunkName.Split("_")[0], StringComparison.OrdinalIgnoreCase))
                        {
                            progress.Report(new(0, true));
                        }
                    }
                }
            }
        }
    }

    private static async ValueTask MergeAssetAsync(AssetProperty assetProperty, GamePackageOperationContext context, CancellationToken token = default)
    {
        string path = Path.Combine(context.GameFileSystem.GameDirectory, assetProperty.AssetName);
        string? directory = Path.GetDirectoryName(path);
        ArgumentNullException.ThrowIfNull(directory);
        Directory.CreateDirectory(directory);
        //using (FileStream file = File.Create(path))
        //{
        //    foreach (AssetChunk chunk in assetProperty.AssetChunks)
        //    {
        //        string chunkPath = Path.Combine(context.GameFileSystem.ChunksDirectory, chunk.ChunkName);
        //        using (FileStream chunkFile = File.OpenRead(chunkPath))
        //        {
        //            using (ZstandardDecompressionStream decompressionStream = new(chunkFile))
        //            {
        //                file.Position = chunk.ChunkOnFileOffset;
        //                await decompressionStream.CopyToAsync(file, token).ConfigureAwait(false);
        //            }
        //        }
        //    }
        //}

        using (SafeFileHandle fileHandle = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None, preallocationSize: 32 * 1024))
        {
            using (IMemoryOwner<byte> memoryOwner = MemoryPool<byte>.Shared.Rent(81920))
            {
                Memory<byte> buffer = memoryOwner.Memory;
                foreach (AssetChunk chunk in assetProperty.AssetChunks)
                {
                    string chunkPath = Path.Combine(context.GameFileSystem.ChunksDirectory, chunk.ChunkName);
                    using (FileStream chunkFile = File.OpenRead(chunkPath))
                    {
                        using (ZstandardDecompressionStream decompressionStream = new(chunkFile))
                        {
                            long offset = chunk.ChunkOnFileOffset;
                            do
                            {
                                int bytesRead = await decompressionStream.ReadAsync(buffer, token).ConfigureAwait(false);
                                if (bytesRead <= 0)
                                {
                                    break;
                                }

                                await RandomAccess.WriteAsync(fileHandle, buffer[..bytesRead], offset, token).ConfigureAwait(false);
                                offset += bytesRead;
                            }
                            while (true);
                        }
                    }
                }
            }
        }
    }

    private async ValueTask MergeDiffAssetAsync(AssetProperty oldAsset, SophonAsset newAsset, GamePackageOperationContext context, CancellationToken token = default)
    {
        using (MemoryStream newAssetStream = memoryStreamFactory.GetStream())
        {
            //using (FileStream oldAssetStream = File.OpenRead(Path.Combine(context.GameFileSystem.GameDirectory, oldAsset.AssetName)))
            //{
            //    foreach (AssetChunk chunk in newAsset.AssetProperty.AssetChunks)
            //    {
            //        newAssetStream.Position = chunk.ChunkOnFileOffset;

            //        AssetChunk? oldChunk = oldAsset.AssetChunks.FirstOrDefault(c => c.ChunkDecompressedHashMd5 == chunk.ChunkDecompressedHashMd5);
            //        if (oldChunk is null)
            //        {
            //            using (FileStream diffStream = File.OpenRead(Path.Combine(context.GameFileSystem.ChunksDirectory, chunk.ChunkName)))
            //            {
            //                using (ZstandardDecompressionStream decompressionStream = new(diffStream))
            //                {
            //                    await decompressionStream.CopyToAsync(newAssetStream, token).ConfigureAwait(false);
            //                    continue;
            //                }
            //            }
            //        }

            //        oldAssetStream.Position = oldChunk.ChunkOnFileOffset;
            //        using (IMemoryOwner<byte> memoryOwner = MemoryPool<byte>.Shared.Rent(81920))
            //        {
            //            Memory<byte> buffer = memoryOwner.Memory;
            //            long bytesToCopy = oldChunk.ChunkSizeDecompressed;
            //            while (bytesToCopy > 0)
            //            {
            //                int bytesRead = await oldAssetStream.ReadAsync(buffer[..(int)Math.Min(buffer.Length, bytesToCopy)], token).ConfigureAwait(false);
            //                if (bytesRead <= 0)
            //                {
            //                    break;
            //                }

            //                await newAssetStream.WriteAsync(buffer[..bytesRead], token).ConfigureAwait(false);
            //                bytesToCopy -= bytesRead;
            //            }
            //        }
            //    }

            //    using (FileStream newAssetFileStream = File.Create(Path.Combine(context.GameFileSystem.GameDirectory, newAsset.AssetProperty.AssetName)))
            //    {
            //        newAssetStream.Position = 0;
            //        await newAssetStream.CopyToAsync(newAssetFileStream, token).ConfigureAwait(false);
            //    }
            //}

            using (SafeFileHandle oldAssetHandle = File.OpenHandle(Path.Combine(context.GameFileSystem.GameDirectory, oldAsset.AssetName), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                foreach (AssetChunk chunk in newAsset.AssetProperty.AssetChunks)
                {
                    newAssetStream.Position = chunk.ChunkOnFileOffset;

                    AssetChunk? oldChunk = oldAsset.AssetChunks.FirstOrDefault(c => c.ChunkDecompressedHashMd5 == chunk.ChunkDecompressedHashMd5);
                    if (oldChunk is null)
                    {
                        using (FileStream diffStream = File.OpenRead(Path.Combine(context.GameFileSystem.ChunksDirectory, chunk.ChunkName)))
                        {
                            using (ZstandardDecompressionStream decompressionStream = new(diffStream))
                            {
                                await decompressionStream.CopyToAsync(newAssetStream, token).ConfigureAwait(false);
                                continue;
                            }
                        }
                    }

                    using (IMemoryOwner<byte> memoryOwner = MemoryPool<byte>.Shared.Rent(81920))
                    {
                        Memory<byte> buffer = memoryOwner.Memory;
                        long offset = oldChunk.ChunkOnFileOffset;
                        long bytesToCopy = oldChunk.ChunkSizeDecompressed;
                        while (bytesToCopy > 0)
                        {
                            int bytesRead = await RandomAccess.ReadAsync(oldAssetHandle, buffer[..(int)Math.Min(buffer.Length, bytesToCopy)], offset, token).ConfigureAwait(false);
                            if (bytesRead <= 0)
                            {
                                break;
                            }

                            await newAssetStream.WriteAsync(buffer[..bytesRead], token).ConfigureAwait(false);
                            offset += bytesRead;
                            bytesToCopy -= bytesRead;
                        }
                    }
                }

                using (FileStream newAssetFileStream = File.Create(Path.Combine(context.GameFileSystem.GameDirectory, newAsset.AssetProperty.AssetName)))
                {
                    newAssetStream.Position = 0;
                    await newAssetStream.CopyToAsync(newAssetFileStream, token).ConfigureAwait(false);
                }
            }
        }
    }

    #endregion

    #region Utils

    private static long GetDiskAvailableFreeSpace(GameFileSystem gameFileSystem)
    {
        string? disk = Path.GetPathRoot(gameFileSystem.GameDirectory);
        ArgumentNullException.ThrowIfNull(disk);
        DriveInfo driveInfo = new(disk);
        return driveInfo.AvailableFreeSpace;
    }

    #endregion
}