using CommunityToolkit.HighPerformance.Helpers;
using CUE4Parse.ACL;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Animation.ACL;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine.Curves;
using CUE4Parse.UE4.Readers;
using CUE4Parse.Utils;
using pxr;
using System.Runtime.InteropServices;

public static class UAnimSequenceToUSD
{
    public static void ConvertAnimationToUsd(UAnimSequence animSequence, USkeleton skeleton, string outputDirectory)
    {
        if (animSequence == null) throw new ArgumentNullException(nameof(animSequence));
        if (skeleton == null) throw new ArgumentNullException(nameof(skeleton));
        if (string.IsNullOrEmpty(outputDirectory)) throw new ArgumentNullException(nameof(outputDirectory));

        var originalAssetName = skeleton.Name ?? "UnnamedSkeleton";
        var sanitizedPrimName = USkeletalMeshToUSD.SanitizeUsdName(originalAssetName);

        // ルートディレクトリ: outputDirectory/CharacterName/
        var rootDir = Path.Combine(outputDirectory, "Anim", sanitizedPrimName);

        // Animsディレクトリ: outputDirectory/CharacterName/Skel/Anims/
        //var animsDir = Path.Combine(rootDir, "Skel", "Anims");
        var animsDir = rootDir;

        Directory.CreateDirectory(animsDir);

        // アニメーションファイル名: CharacterName_AnimName_anim.usda
        var animName = animSequence.Name ?? "UnnamedAnimation";
        var sanitizedAnimName = USkeletalMeshToUSD.SanitizeUsdName(animName);
        var animFile = Path.Combine(animsDir, sanitizedAnimName + "_anim.usda");

        // 使用ボーンインデックスを取得（アニメーションから、optimizeBonesに基づく）
        var sourceBoneInfo = skeleton.ReferenceSkeleton.FinalRefBoneInfo;
        var usedBoneIndices = Enumerable.Range(0, sourceBoneInfo.Length).ToList();

        // USDボーンパスを作成
        var usdBones = USkeletalMeshToUSD.BuildUsdBonePaths(sourceBoneInfo, usedBoneIndices);

        // アニメーションUSDを書き出す
        WriteAnimUsd(animFile, sanitizedAnimName, animSequence, skeleton, usdBones, usedBoneIndices);

        Console.WriteLine($"Wrote Animation USD: {animFile}");
    }
    private static void WriteAnimUsd(string filePath, string animName, UAnimSequence animSequence, USkeleton skeleton, VtTokenArray usdBones, List<int> usedBoneIndices)
    {
        using (var stage = UsdStage.CreateNew(filePath))
        {
            if (stage == null) throw new InvalidOperationException($"Failed to create Animation USD: {filePath}");

            UsdGeom.UsdGeomSetStageUpAxis(stage, UsdGeomTokens.y);
            UsdGeom.UsdGeomSetStageMetersPerUnit(stage, 1);


            // アニメーションスコープ: /Anims
            var animScopePath = new SdfPath("/Anims");
            var animScope = UsdGeomScope.Define(stage, animScopePath);
            stage.SetDefaultPrim(animScope.GetPrim());

            // UsdSkelAnimation Prim: /Anims/<animName>
            var animPrimPath = animScope.GetPath().AppendChild(new TfToken(animName));
            var usdAnim = UsdSkelAnimation.Define(stage, animPrimPath);

            // joints 属性を設定（メッシュのスケルトンと同じ）
            usdAnim.CreateJointsAttr().Set(usdBones);
            var jointNamesList = new List<string>();
            for (int i = 0; i < usdBones.size(); i++)
            {
                string fullPath = usdBones[i].GetText();
                string boneName = fullPath.Split('/').LastOrDefault();
                if (!string.IsNullOrEmpty(boneName))
                    jointNamesList.Add(boneName);
            }
            var jointNames = jointNamesList.ToArray();
            var usdJointNames = new VtStringArray();
            foreach (var name in jointNames)
            {
                usdJointNames.push_back(name);
            }

            usdAnim.GetPrim().CreateAttribute(new TfToken(USkeletalMeshToUSD.OriginalJointNamesAttribute), SdfValueTypeNames.StringArray).Set(usdJointNames);
            usdAnim.GetPrim().CreateAttribute(new TfToken(USkeletalMeshToUSD.OriginalSkeletonNameAttribute), SdfValueTypeNames.String).Set(skeleton.Name);
            usdAnim.GetPrim().CreateAttribute(new TfToken(USkeletalMeshToUSD.OriginalSkeletonPathAttribute), SdfValueTypeNames.String).Set(skeleton.GetPathName());

            // アニメーションデータを抽出
            ProcessAnimationData(animSequence, skeleton, usdAnim, usedBoneIndices, stage);

            stage.GetRootLayer().Save();
        }
    }


    private static void ProcessAnimationData(UAnimSequence animSequence, USkeleton skeleton, UsdSkelAnimation usdAnim, List<int> usedBoneIndices, UsdStage stage)
    {
        // アニメーションのフレーム数とレートを取得
        int sequenceNumFrames = animSequence.NumFrames;
        float secondsPerFrame = animSequence.SequenceLength / (sequenceNumFrames - 1); // 秒単位のフレーム間隔
        float floatFps = 1f / secondsPerFrame;
        int fps = (int)Math.Round(floatFps);
        int endFrame = sequenceNumFrames;

        // 現フレームの秒数
        var timeCodes = new List<float>(); // Will be populated later based on sparse or dense

        int numBones = usedBoneIndices.Count;

        // スパースデータ用のリスト
        var sparseTranslations = new List<(double Time, VtVec3fArray Values)>();
        var sparseRotations = new List<(double Time, VtQuatfArray Values)>();
        var sparseScales = new List<(double Time, VtVec3hArray Values)>();

        // UAnimSequenceからトラックデータを抽出
        var rawAnimData = animSequence.RawAnimationData;
        if (rawAnimData == null)
        {
            switch (animSequence.CompressedDataStructure)
            {
                case FUECompressedAnimData ueData:
                    {
                        if (ueData.KeyEncodingFormat == AnimationKeyFormat.AKF_PerTrackCompression)
                        {
                            ProcessPerTrackCompression(ueData, animSequence, skeleton, usedBoneIndices, ref timeCodes, ref sparseTranslations, ref sparseRotations, ref sparseScales, ref endFrame, ref fps, secondsPerFrame);
                        }
                        else
                        {
                            ProcessKeyLerpData(ueData, animSequence, skeleton, usedBoneIndices, ref timeCodes, ref sparseTranslations, ref sparseRotations, ref sparseScales, ref endFrame, ref fps, secondsPerFrame);
                        }
                        break;
                    }
                case FACLCompressedAnimData aclData:
                    {
                        ProcessACLCompression(aclData, animSequence, skeleton, usedBoneIndices, ref timeCodes, ref sparseTranslations, ref sparseRotations, ref sparseScales, ref endFrame, ref fps, ref secondsPerFrame);
                        break;
                    }
                default:
                    throw new ArgumentOutOfRangeException("Unsupported compressed data type " + animSequence.CompressedDataStructure?.GetType().Name);
            }
        }
        else
        {
            throw new NotImplementedException("RawAnimationData is not supported in this implementation.");
        }

        // 1からendFrameまでのtimeCodesを設定
        stage.SetStartTimeCode(1);
        stage.SetEndTimeCode(endFrame);
        stage.SetTimeCodesPerSecond(fps);
        stage.SetFramesPerSecond(fps);

        // timeSamplesを設定
        var translationsAttr = usdAnim.CreateTranslationsAttr();
        var rotationsAttr = usdAnim.CreateRotationsAttr();
        var scalesAttr = usdAnim.CreateScalesAttr();

        // スパースデータをUSDに書き込み
        foreach (var (time, translations) in sparseTranslations)
        {
            translationsAttr.Set(translations, new UsdTimeCode(time));
        }

        foreach (var (time, rotations) in sparseRotations)
        {
            rotationsAttr.Set(rotations, new UsdTimeCode(time));
        }

        foreach (var (time, scales) in sparseScales)
        {
            scalesAttr.Set(scales, new UsdTimeCode(time));
        }

        // Handle blend shapes if available (unchanged, as it's already independent)
        if (animSequence.CompressedCurveByteStream.Length > 0)
        {
            var blendShapeNames = animSequence.CompressedCurveNames;
            VtTokenArray usdBlendShapeNames = new VtTokenArray((uint)blendShapeNames.Length);
            for (int i = 0; i < blendShapeNames.Length; i++)
            {
                usdBlendShapeNames[i] = new TfToken(blendShapeNames[i].DisplayName.Text);
            }

            usdAnim.CreateBlendShapesAttr().Set(usdBlendShapeNames);

            var blendShapes = animSequence.CompressedCurveData;
            var sparseBlendShapeWeights = new List<(double Time, VtFloatArray Weights)>();

            // Initialize weights array for each frame
            var prevWeights = new VtFloatArray((uint)blendShapeNames.Length);
            bool isFirstFrame = true;

            // ブレンドシェイプ用に最後に追加した時間コードを追跡
            double lastAddedBlendShapeTime = -1;
            double prevBlendFrameTime = 0;

            for (int f = 0; f < timeCodes.Count; f++)
            {
                float time = timeCodes[f];
                double frameTime = TimeToFrame(time, secondsPerFrame);
                var weights = new VtFloatArray((uint)blendShapeNames.Length);
                bool hasChanges = false;

                for (int i = 0; i < blendShapeNames.Length; i++)
                {
                    weights[i] = 0.0f; // Default weight
                }

                foreach (var curve in blendShapes.FloatCurves)
                {
                    int blendShapeIndex = Array.FindIndex(blendShapeNames, name => name.DisplayName.Text == curve.CurveName.Text);
                    if (blendShapeIndex < 0) continue;

                    var keys = curve.FloatCurve.Keys;
                    if (keys == null || keys.Length == 0) continue;

                    float weight = InterpolateBlendShapeWeight(keys, time);
                    weights[blendShapeIndex] = weight;

                    if (isFirstFrame || Math.Abs(weights[blendShapeIndex] - prevWeights[blendShapeIndex]) > 0.0001f)
                    {
                        hasChanges = true;
                    }
                }

                if (isFirstFrame || hasChanges)
                {
                    if (!isFirstFrame && lastAddedBlendShapeTime != prevBlendFrameTime)
                    {
                        sparseBlendShapeWeights.Add((prevBlendFrameTime, prevWeights));
                        lastAddedBlendShapeTime = prevBlendFrameTime;
                    }
                    sparseBlendShapeWeights.Add((frameTime, weights));
                    lastAddedBlendShapeTime = frameTime;
                }

                prevWeights = weights;
                prevBlendFrameTime = frameTime;
                isFirstFrame = false;
            }

            var blendShapeWeightsAttr = usdAnim.CreateBlendShapeWeightsAttr();
            foreach (var (time, weights) in sparseBlendShapeWeights)
            {
                blendShapeWeightsAttr.Set(weights, new UsdTimeCode(time));
            }
        }

        // 補間タイプをカスタム属性として保存
        string interpolationType = animSequence.Interpolation == CUE4Parse.UE4.Assets.Exports.Animation.EAnimInterpolationType.Linear ? "linear" : "held";
        var interpolationAttr = usdAnim.GetPrim().CreateAttribute(new TfToken("ue:interpolationType"), SdfValueTypeNames.String);
        interpolationAttr.Set(interpolationType);

        // 補間タイプを設定 Runtimeに反映?
        if (animSequence.Interpolation == CUE4Parse.UE4.Assets.Exports.Animation.EAnimInterpolationType.Linear)
        {
            stage.SetInterpolationType(UsdInterpolationType.UsdInterpolationTypeLinear);
        }
        else if (animSequence.Interpolation == CUE4Parse.UE4.Assets.Exports.Animation.EAnimInterpolationType.Step)
        {
            stage.SetInterpolationType(UsdInterpolationType.UsdInterpolationTypeHeld);
        }
    }

    // ACL圧縮形式の処理
    private static void ProcessACLCompression(FACLCompressedAnimData aclData, UAnimSequence animSequence, USkeleton skeleton, List<int> usedBoneIndices,
        ref List<float> timeCodes, ref List<(double Time, VtVec3fArray Values)> sparseTranslations, ref List<(double Time, VtQuatfArray Values)> sparseRotations,
        ref List<(double Time, VtVec3hArray Values)> sparseScales, ref int endFrame, ref int fps, ref float secondsPerFrame)
    {
        // 1. ACLデータのデコードと基本情報の設定
        var tracks = aclData.GetCompressedTracks();
        var tracksHeader = tracks.GetTracksHeader();
        var numSamples = (int)tracksHeader.NumSamples;

        endFrame = numSamples;
        fps = (int)tracksHeader.SampleRate;
        // ref パラメーターに値を書き込む
        secondsPerFrame = 1f / tracksHeader.SampleRate;

        // ref パラメーターの値をローカル変数にコピーする
        float localSecondsPerFrame = secondsPerFrame;

        // コピーしたローカル変数を使用して timeCodes を生成する
        timeCodes = Enumerable.Range(0, numSamples).Select(f => f * localSecondsPerFrame).ToList();

        tracks.SetDefaultScale(1);
        var atomKeys = new FTransform[tracksHeader.NumTracks * numSamples];
        unsafe
        {
            fixed (FTransform* refPosePtr = skeleton.ReferenceSkeleton.FinalRefBonePose)
            fixed (FTrackToSkeletonMap* trackToSkeletonMapPtr = animSequence.GetTrackMap())
            fixed (FTransform* atomKeysPtr = atomKeys)
            {
                nReadACLData(tracks.Handle, refPosePtr, trackToSkeletonMapPtr, atomKeysPtr);
            }
        }

        // 2. 特定時間のトランスフォームを取得するデリゲートを定義
        Func<float, int, (FVector Translation, FQuat Rotation, FVector Scale)> getTransformAtTime = (time, boneIndex) =>
        {
            // ref パラメーターではなく、コピーしたローカル変数を使用する
            var frame = Math.Clamp((int)Math.Round(time / localSecondsPerFrame), 0, numSamples - 1);
            var trackIndex = animSequence.FindTrackForBoneIndex(boneIndex);

            FTransform transform = (trackIndex >= 0)
                ? atomKeys[trackIndex * numSamples + frame]
                : skeleton.ReferenceSkeleton.FinalRefBonePose[boneIndex];

            return (transform.Translation, transform.Rotation, transform.Scale3D);
        };

        // 3. 共通のスパースキー生成処理を呼び出す
        CreateSparseKeys(timeCodes, localSecondsPerFrame, usedBoneIndices, getTransformAtTime,
            ref sparseTranslations, ref sparseRotations, ref sparseScales);
    }

    // Per-Track圧縮形式の処理
    private static void ProcessPerTrackCompression(FUECompressedAnimData ueData, UAnimSequence animSequence, USkeleton skeleton, List<int> usedBoneIndices,
        ref List<float> timeCodes, ref List<(double Time, VtVec3fArray Values)> sparseTranslations, ref List<(double Time, VtQuatfArray Values)> sparseRotations,
        ref List<(double Time, VtVec3hArray Values)> sparseScales, ref int endFrame, ref int fps, float secondsPerFrame)
    {
        // 共通処理メソッドに、Per-Track用のデコードロジックを渡して実行
        ProcessUECompressedData(DecompressPerTrack, ueData, animSequence, skeleton, usedBoneIndices,
            ref timeCodes, ref sparseTranslations, ref sparseRotations, ref sparseScales, ref endFrame, secondsPerFrame);
    }

    // Key-Lerp圧縮形式の処理
    private static void ProcessKeyLerpData(FUECompressedAnimData ueData, UAnimSequence animSequence, USkeleton skeleton, List<int> usedBoneIndices,
        ref List<float> timeCodes, ref List<(double Time, VtVec3fArray Values)> sparseTranslations, ref List<(double Time, VtQuatfArray Values)> sparseRotations,
        ref List<(double Time, VtVec3hArray Values)> sparseScales, ref int endFrame, ref int fps, float secondsPerFrame)
    {
        // 共通処理メソッドに、Key-Lerp用のデコードロジックを渡して実行
        ProcessUECompressedData(DecompressKeyLerp, ueData, animSequence, skeleton, usedBoneIndices,
            ref timeCodes, ref sparseTranslations, ref sparseRotations, ref sparseScales, ref endFrame, secondsPerFrame);
    }

    // トラックデコード処理のデリゲート定義
    private delegate void TrackDecompressionLogic(
        FByteArchive reader, UAnimSequence animSequence, FUECompressedAnimData ueData, List<int> usedBoneIndices, float secondsPerFrame,
        out Dictionary<int, List<(float Time, FVector Value)>> translationTracksCache,
        out Dictionary<int, List<(float Time, FQuat Value)>> rotationTracksCache,
        out Dictionary<int, List<(float Time, FVector Value)>> scaleTracksCache,
        out HashSet<uint> uniqueFrameNumbers);

    // PerTrack と KeyLerp のための共通処理メソッド
    private static void ProcessUECompressedData(
        TrackDecompressionLogic decompressor,
        FUECompressedAnimData ueData, UAnimSequence animSequence, USkeleton skeleton, List<int> usedBoneIndices,
        ref List<float> timeCodes, ref List<(double Time, VtVec3fArray Values)> sparseTranslations, ref List<(double Time, VtQuatfArray Values)> sparseRotations,
        ref List<(double Time, VtVec3hArray Values)> sparseScales, ref int endFrame, float secondsPerFrame)
    {
        // 1. デリゲートを使ってトラックデータをデコード・キャッシュする
        using var reader = new FByteArchive("CompressedByteStream", ueData.CompressedByteStream);
        decompressor(reader, animSequence, ueData, usedBoneIndices, secondsPerFrame,
            out var translationTracksCache, out var rotationTracksCache, out var scaleTracksCache,
            out var uniqueFrameNumbers);

        // 2. ユニークなキーフレーム時間からtimeCodesを生成
        timeCodes = uniqueFrameNumbers
            .Select(frame => (frame > 0 ? frame - 1 : 0) * secondsPerFrame)
            .Distinct()
            .OrderBy(time => time)
            .ToList();
        endFrame = uniqueFrameNumbers.Count > 0 ? (int)uniqueFrameNumbers.Max() : 0;

        // 3. 補間を行うデータ取得デリゲートを作成
        Func<float, int, (FVector, FQuat, FVector)> getTransformAtTime = (time, boneIndex) =>
        {
            var refPose = skeleton.ReferenceSkeleton.FinalRefBonePose[boneIndex];
            translationTracksCache.TryGetValue(boneIndex, out var translationKeys);
            rotationTracksCache.TryGetValue(boneIndex, out var rotationKeys);
            scaleTracksCache.TryGetValue(boneIndex, out var scaleKeys);

            var translation = GetInterpolatedTranslation(translationKeys, time, refPose.Translation);
            var rotation = GetInterpolatedRotation(rotationKeys, time, refPose.Rotation);
            var scale = GetInterpolatedTranslation(scaleKeys, time, refPose.Scale3D);

            return (translation, rotation, scale);
        };

        // 4. 共通のスパースキー生成処理を呼び出す
        CreateSparseKeys(timeCodes, secondsPerFrame, usedBoneIndices, getTransformAtTime,
            ref sparseTranslations, ref sparseRotations, ref sparseScales);
    }

    // Per-Track形式のデコードロジック
    private static void DecompressPerTrack(FByteArchive reader, UAnimSequence animSequence, FUECompressedAnimData ueData, List<int> usedBoneIndices, float secondsPerFrame,
        out Dictionary<int, List<(float Time, FVector Value)>> translationTracksCache,
        out Dictionary<int, List<(float Time, FQuat Value)>> rotationTracksCache,
        out Dictionary<int, List<(float Time, FVector Value)>> scaleTracksCache,
        out HashSet<uint> uniqueFrameNumbers)
    {
        translationTracksCache = new Dictionary<int, List<(float, FVector)>>();
        rotationTracksCache = new Dictionary<int, List<(float, FQuat)>>();
        scaleTracksCache = new Dictionary<int, List<(float, FVector)>>();
        uniqueFrameNumbers = new HashSet<uint> { 1, (uint)animSequence.NumFrames };

        var numTracks = animSequence.GetNumTracks();
        for (int trackIndex = 0; trackIndex < numTracks; trackIndex++)
        {
            int boneIndex = animSequence.GetTrackBoneIndex(trackIndex);
            if (boneIndex < 0) continue;

            // トランスレーション
            int transOffset = ueData.CompressedTrackOffsets[trackIndex * 2];
            if (transOffset >= 0)
            {
                reader.Position = transOffset;
                translationTracksCache[boneIndex] = DecompressTranslationTrack(reader, transOffset, animSequence.SequenceLength, animSequence.NumFrames, out var frameNumbers, out _);
                if (frameNumbers != null) uniqueFrameNumbers.UnionWith(frameNumbers);
            }
            // ローテーション
            int rotOffset = ueData.CompressedTrackOffsets[trackIndex * 2 + 1];
            if (rotOffset >= 0)
            {
                reader.Position = rotOffset;
                rotationTracksCache[boneIndex] = DecompressRotationTrack(reader, rotOffset, animSequence.SequenceLength, animSequence.NumFrames, out var frameNumbers, out _);
                if (frameNumbers != null) uniqueFrameNumbers.UnionWith(frameNumbers);
            }
            // スケール
            if (ueData.CompressedScaleOffsets?.IsValid() == true && trackIndex < ueData.CompressedScaleOffsets.OffsetData.Length)
            {
                int scaleOffset = ueData.CompressedScaleOffsets.OffsetData[trackIndex];
                if (scaleOffset != -1 && scaleOffset < ueData.CompressedByteStream.Count())
                {
                    reader.Position = scaleOffset;
                    scaleTracksCache[boneIndex] = DecompressTranslationTrack(reader, scaleOffset, animSequence.SequenceLength, animSequence.NumFrames, out var frameNumbers, out _);
                    if (frameNumbers != null) uniqueFrameNumbers.UnionWith(frameNumbers);
                }
            }
        }
    }

    // Key-Lerp形式のデコードロジック
    private static void DecompressKeyLerp(FByteArchive reader, UAnimSequence animSequence, FUECompressedAnimData ueData, List<int> usedBoneIndices, float secondsPerFrame,
        out Dictionary<int, List<(float Time, FVector Value)>> translationTracksCache,
        out Dictionary<int, List<(float Time, FQuat Value)>> rotationTracksCache,
        out Dictionary<int, List<(float Time, FVector Value)>> scaleTracksCache,
        out HashSet<uint> uniqueFrameNumbers)
    {
        translationTracksCache = new Dictionary<int, List<(float, FVector)>>();
        rotationTracksCache = new Dictionary<int, List<(float, FQuat)>>();
        scaleTracksCache = new Dictionary<int, List<(float, FVector)>>();
        uniqueFrameNumbers = new HashSet<uint> { 1, (uint)animSequence.NumFrames };

        bool hasTimeTracks = (ueData.KeyEncodingFormat == AnimationKeyFormat.AKF_VariableKeyLerp);

        var numTracks = animSequence.GetNumTracks();
        for (int trackIndex = 0; trackIndex < numTracks; trackIndex++)
        {
            int boneIndex = animSequence.GetTrackBoneIndex(trackIndex);
            if (boneIndex < 0 || !usedBoneIndices.Contains(boneIndex)) continue;

            int transOffset = ueData.CompressedTrackOffsets[trackIndex * 4 + 0], transKeys = ueData.CompressedTrackOffsets[trackIndex * 4 + 1];
            int rotOffset = ueData.CompressedTrackOffsets[trackIndex * 4 + 2], rotKeys = ueData.CompressedTrackOffsets[trackIndex * 4 + 3];

            if (transKeys > 0 && transOffset >= 0)
            {
                translationTracksCache[boneIndex] = ReadVectorKeys(reader, transOffset, transKeys, ueData.TranslationCompressionFormat, hasTimeTracks, animSequence.NumFrames, secondsPerFrame, out var frameNumbers);
                uniqueFrameNumbers.UnionWith(frameNumbers);
            }
            if (rotKeys > 0 && rotOffset >= 0)
            {
                rotationTracksCache[boneIndex] = ReadRotationKeys(reader, rotOffset, rotKeys, ueData.RotationCompressionFormat, hasTimeTracks, animSequence.NumFrames, secondsPerFrame, out var frameNumbers);
                uniqueFrameNumbers.UnionWith(frameNumbers);
            }
            if (ueData.CompressedScaleOffsets.IsValid() && trackIndex * 2 < ueData.CompressedScaleOffsets.OffsetData.Length)
            {
                int scaleOffset = ueData.CompressedScaleOffsets.OffsetData[trackIndex * 2], scaleKeys = ueData.CompressedScaleOffsets.OffsetData[trackIndex * 2 + 1];
                if (scaleKeys > 0 && scaleOffset >= 0)
                {
                    scaleTracksCache[boneIndex] = ReadVectorKeys(reader, scaleOffset, scaleKeys, ueData.ScaleCompressionFormat, hasTimeTracks, animSequence.NumFrames, secondsPerFrame, out var frameNumbers);
                    uniqueFrameNumbers.UnionWith(frameNumbers);
                }
            }
        }
    }

    // 全ての形式で共通のスパースキー生成コアロジック
    private static void CreateSparseKeys(
        List<float> timeCodes, float secondsPerFrame, List<int> usedBoneIndices,
        Func<float, int, (FVector Translation, FQuat Rotation, FVector Scale)> getTransformAtTime,
        ref List<(double Time, VtVec3fArray Values)> sparseTranslations,
        ref List<(double Time, VtQuatfArray Values)> sparseRotations,
        ref List<(double Time, VtVec3hArray Values)> sparseScales)
    {
        var prevTranslations = new VtVec3fArray((uint)usedBoneIndices.Count);
        var prevRotations = new VtQuatfArray((uint)usedBoneIndices.Count);
        var prevScales = new VtVec3hArray((uint)usedBoneIndices.Count);

        double lastAddedTranslationTime = -1, lastAddedRotationTime = -1, lastAddedScaleTime = -1;
        double prevFrameTime = 0;
        bool isFirstFrame = true;

        foreach (float time in timeCodes)
        {
            double frameTime = TimeToFrame(time, secondsPerFrame);
            var (translations, rotations, scales) = (new VtVec3fArray((uint)usedBoneIndices.Count), new VtQuatfArray((uint)usedBoneIndices.Count), new VtVec3hArray((uint)usedBoneIndices.Count));
            bool translationsChanged = false, rotationsChanged = false, scalesChanged = false;

            // 現フレームの全ボーンのTRS 取得
            for (var boneIdx = 0; boneIdx < usedBoneIndices.Count; boneIdx++)
            {
                // デリゲートメソッドで現フレームの現ボーンのTRS取得
                var (ueTranslation, ueRotation, ueScale) = getTransformAtTime(time, usedBoneIndices[boneIdx]);

                var usdPos = UsdCoordinateTransformer.TransformPosition(ueTranslation * USkeletalMeshToUSD.UeToUsdScale);
                translations[boneIdx] = new GfVec3f(usdPos.X, usdPos.Y, usdPos.Z);
                var usdRot = UsdCoordinateTransformer.TransformRotation(ueRotation);
                rotations[boneIdx] = new GfQuatf(usdRot.W, usdRot.X, usdRot.Y, usdRot.Z);
                scales[boneIdx] = new GfVec3h(new GfVec3f(ueScale.X, ueScale.Y, ueScale.Z));

                if (isFirstFrame || !translations[boneIdx].Equals(prevTranslations[boneIdx])) translationsChanged = true;
                if (isFirstFrame || !rotations[boneIdx].Equals(prevRotations[boneIdx])) rotationsChanged = true;
                if (isFirstFrame || !scales[boneIdx].Equals(prevScales[boneIdx])) scalesChanged = true;
            }

            AddKeyIfChanged(sparseTranslations, translations, frameTime, translationsChanged, isFirstFrame, ref lastAddedTranslationTime, prevFrameTime, prevTranslations);
            AddKeyIfChanged(sparseRotations, rotations, frameTime, rotationsChanged, isFirstFrame, ref lastAddedRotationTime, prevFrameTime, prevRotations);
            AddKeyIfChanged(sparseScales, scales, frameTime, scalesChanged, isFirstFrame, ref lastAddedScaleTime, prevFrameTime, prevScales);

            prevTranslations = translations;
            prevRotations = rotations;
            prevScales = scales;
            prevFrameTime = frameTime;
            isFirstFrame = false;
        }
    }

    // 汎用ヘルパーメソッド：変更があった場合にスパースキーリストにキーを追加する
    private static void AddKeyIfChanged<T>(
        List<(double Time, T Values)> sparseKeys, T currentValues, double currentTime, bool hasChanged, bool isFirstFrame,
        ref double lastAddedTime, double prevTime, T prevValues) where T : class
    {
        if (hasChanged)
        {
            if (!isFirstFrame && lastAddedTime != prevTime)
            {
                sparseKeys.Add((prevTime, prevValues));
                lastAddedTime = prevTime;
            }
            sparseKeys.Add((currentTime, currentValues));
            lastAddedTime = currentTime;
        }
    }
    // ベクターキー（トランスレーション、スケール）の読み込み
    private static List<(float Time, FVector Value)> ReadVectorKeys(FArchive reader, int offset, int numKeys, AnimationCompressionFormat compressionFormat,
        bool hasTimeTracks, int numFrames, float secondsPerFrame, out List<uint> frameNumbers)
    {
        frameNumbers = new List<uint>();
        if (numKeys == 0 || offset < 0) return new List<(float, FVector)>();

        reader.Position = offset;
        if (numKeys == 1) compressionFormat = AnimationCompressionFormat.ACF_None;

        FVector mins = FVector.ZeroVector;
        FVector ranges = FVector.ZeroVector;
        if (compressionFormat == AnimationCompressionFormat.ACF_IntervalFixed32NoW)
        {
            mins = reader.Read<FVector>();
            ranges = reader.Read<FVector>();
        }

        var keys = new FVector[numKeys];
        for (var keyIndex = 0; keyIndex < numKeys; keyIndex++)
        {
            keys[keyIndex] = compressionFormat switch
            {
                AnimationCompressionFormat.ACF_None => reader.Read<FVector>(),
                AnimationCompressionFormat.ACF_Float96NoW => reader.Read<FVector>(),
                AnimationCompressionFormat.ACF_IntervalFixed32NoW => reader.ReadVectorIntervalFixed32(mins, ranges),
                AnimationCompressionFormat.ACF_Fixed48NoW => reader.ReadVectorFixed48(),
                AnimationCompressionFormat.ACF_Identity => FVector.ZeroVector,
                _ => throw new ParserException($"Unknown vector key compression method: {(int)compressionFormat} ({compressionFormat})")
            };
        }

        reader.Position = reader.Position.Align(4);
        float[] timeKeys = null;
        if (hasTimeTracks)
        {
            ReadTimeArray(reader, numKeys, out timeKeys, numFrames);
        }

        var result = new List<(float Time, FVector Value)>(numKeys);
        for (int i = 0; i < numKeys; i++)
        {
            float time = hasTimeTracks && timeKeys != null ? timeKeys[i] * secondsPerFrame : i * secondsPerFrame;
            result.Add((time, keys[i]));
            frameNumbers.Add(TimeToFrame(time, secondsPerFrame));
        }

        return result;
    }

    // ローテーションキーの読み込み
    private static List<(float Time, FQuat Value)> ReadRotationKeys(FArchive reader, int offset, int numKeys, AnimationCompressionFormat compressionFormat,
        bool hasTimeTracks, int numFrames, float secondsPerFrame, out List<uint> frameNumbers)
    {
        frameNumbers = new List<uint>();
        if (numKeys == 0 || offset < 0) return new List<(float, FQuat)>();

        reader.Position = offset;
        if (numKeys == 1) compressionFormat = AnimationCompressionFormat.ACF_Float96NoW;

        FVector mins = FVector.ZeroVector;
        FVector ranges = FVector.ZeroVector;
        if (compressionFormat == AnimationCompressionFormat.ACF_IntervalFixed32NoW)
        {
            mins = reader.Read<FVector>();
            ranges = reader.Read<FVector>();
        }

        var keys = new FQuat[numKeys];
        for (var keyIndex = 0; keyIndex < numKeys; keyIndex++)
        {
            keys[keyIndex] = compressionFormat switch
            {
                AnimationCompressionFormat.ACF_None => reader.Read<FQuat>(),
                AnimationCompressionFormat.ACF_Float96NoW => reader.ReadQuatFloat96NoW(),
                AnimationCompressionFormat.ACF_Fixed48NoW => reader.ReadQuatFixed48NoW(),
                AnimationCompressionFormat.ACF_Fixed32NoW => reader.ReadQuatFixed32NoW(),
                AnimationCompressionFormat.ACF_IntervalFixed32NoW => reader.ReadQuatIntervalFixed32NoW(mins, ranges),
                AnimationCompressionFormat.ACF_Float32NoW => reader.ReadQuatFloat32NoW(),
                AnimationCompressionFormat.ACF_Identity => FQuat.Identity,
                _ => throw new ParserException($"Unknown rotation compression method: {(int)compressionFormat} ({compressionFormat})")
            };
        }

        reader.Position = reader.Position.Align(4);
        float[] timeKeys = null;
        if (hasTimeTracks)
        {
            ReadTimeArray(reader, numKeys, out timeKeys, numFrames);
        }

        var result = new List<(float Time, FQuat Value)>(numKeys);
        for (int i = 0; i < numKeys; i++)
        {
            float time = hasTimeTracks && timeKeys != null ? timeKeys[i] * secondsPerFrame : i * secondsPerFrame;
            result.Add((time, keys[i]));
            frameNumbers.Add(TimeToFrame(time, secondsPerFrame));
        }

        return result;
    }


    private static List<(float Time, FVector Value)> DecompressTranslationTrack(FArchive reader, int offsetIndex, float sequenceLength, int numFrames, out List<uint> frameNumbers, out int trackMaxFrame)
    {
        trackMaxFrame = numFrames;
        frameNumbers = new List<uint>(); // 初期化してnullを回避
        if (reader == null) return null;

        reader.Position = offsetIndex;

        uint packedInfo = reader.Read<uint>();

        AnimationCompressionFormat format = (AnimationCompressionFormat)(packedInfo >> 28);
        int componentMask = (int)((packedInfo >> 24) & 0xF);
        int numKeys = (int)(packedInfo & 0xFFFFFF);
        bool hasTimeTracks = (componentMask & 8) != 0;

        if (numKeys == 0) return null;

        if (format == AnimationCompressionFormat.ACF_Identity)
            return new List<(float, FVector)> { (0, new FVector(0, 0, 0)) };

        int preKeySize = (format == AnimationCompressionFormat.ACF_IntervalFixed32NoW) ? 24 : 0;

        // Read pre-key data if any
        FVector minValue = new FVector(0, 0, 0);
        FVector rangeValue = new FVector(1, 1, 1);
        if (preKeySize > 0)
        {
            // ACF_IntervalFixed32NoW
            minValue.X = reader.Read<float>();
            minValue.Y = reader.Read<float>();
            minValue.Z = reader.Read<float>();
            rangeValue.X = reader.Read<float>();
            rangeValue.Y = reader.Read<float>();
            rangeValue.Z = reader.Read<float>();
        }

        List<FVector> values = new List<FVector>(numKeys);

        switch (format)
        {
            case AnimationCompressionFormat.ACF_None:
            case AnimationCompressionFormat.ACF_Float96NoW:
                for (int k = 0; k < numKeys; k++)
                {
                    float x = 0, y = 0, z = 0;
                    if ((componentMask & 1) != 0) x = reader.Read<float>();
                    if ((componentMask & 2) != 0) y = reader.Read<float>();
                    if ((componentMask & 4) != 0) z = reader.Read<float>();
                    values.Add(new FVector(x, y, z));
                }
                break;
            case AnimationCompressionFormat.ACF_IntervalFixed32NoW:
                float xScale = rangeValue.X / 2047.0f;
                float yScale = rangeValue.Y / 2047.0f;
                float zScale = rangeValue.Z / 1023.0f;
                for (int k = 0; k < numKeys; k++)
                {
                    uint packed = reader.Read<uint>();
                    float x = ((packed >> 21) & 0x7FF) * xScale + minValue.X;
                    float y = ((packed >> 10) & 0x7FF) * yScale + minValue.Y;
                    float z = (packed & 0x3FF) * zScale + minValue.Z;
                    values.Add(new FVector(x, y, z));
                }
                break;
            case AnimationCompressionFormat.ACF_Fixed48NoW:
                float scale = 1.0f / (float)ushort.MaxValue;
                for (int k = 0; k < numKeys; k++)
                {
                    ushort xBits = reader.Read<ushort>();
                    ushort yBits = reader.Read<ushort>();
                    ushort zBits = reader.Read<ushort>();
                    float x = (xBits * scale - 0.5f) * 2.0f;
                    float y = (yBits * scale - 0.5f) * 2.0f;
                    float z = (zBits * scale - 0.5f) * 2.0f;
                    values.Add(new FVector(x, y, z));
                }
                break;
            case AnimationCompressionFormat.ACF_Fixed32NoW:
                for (int k = 0; k < numKeys; k++)
                {
                    uint packed = reader.Read<uint>();
                    float x = ((packed >> 21) & 0x7FF) / 2047.0f - 0.5f;
                    float y = ((packed >> 10) & 0x7FF) / 2047.0f - 0.5f;
                    float z = (packed & 0x3FF) / 1023.0f - 0.5f;
                    values.Add(new FVector(x, y, z));
                }
                break;
            case AnimationCompressionFormat.ACF_Float32NoW:
                for (int k = 0; k < numKeys; k++)
                {
                    float val = reader.Read<float>();
                    values.Add(new FVector(val, val, val));
                }
                break;
        }

        // 現フレームの秒数
        List<float> times = new List<float>(numKeys);
        float frameRate = sequenceLength / (numFrames - 1);

        if (hasTimeTracks)
        {
            // align to 4 bytes
            reader.Position = reader.Position.Align(4);
            float[] dstTimeKeys;
            ReadTimeArray(reader, numKeys, out dstTimeKeys, numFrames);
            for (int k = 0; k < numKeys; k++)
            {
                times.Add(dstTimeKeys[k] * frameRate);
                frameNumbers.Add((uint)dstTimeKeys[k] + 1); // フレーム番号は1から開始
            }
        }
        else
        {
            // スパースでない場合、連続したフレーム番号を生成
            for (int k = 0; k < numKeys; k++)
            {
                times.Add(k * frameRate);
                frameNumbers.Add((uint)(k + 1)); // フレーム番号は1から開始
            }
        }

        List<(float, FVector)> result = new List<(float, FVector)>(numKeys);
        for (int k = 0; k < numKeys; k++)
        {
            result.Add((times[k], values[k]));
        }
        return result;
    }
    private static List<(float Time, FQuat Value)> DecompressRotationTrack(FArchive reader, int offsetIndex, float sequenceLength, int numFrames, out List<uint> frameNumbers, out int trackMaxFrame)
    {
        trackMaxFrame = numFrames;
        frameNumbers = new List<uint>(); // 初期化してnullを回避
        if (reader == null) return null;

        reader.Position = offsetIndex;

        uint packedInfo = reader.Read<UInt32>();

        AnimationCompressionFormat format = (AnimationCompressionFormat)(packedInfo >> 28);
        int componentMask = (int)((packedInfo >> 24) & 0xF);
        int numKeys = (int)(packedInfo & 0xFFFFFF);
        bool hasTimeTracks = (componentMask & 8) != 0;

        if (numKeys == 0) return null;

        // Read pre-key data if any
        FVector minValue = new FVector(0, 0, 0);
        FVector rangeValue = new FVector(1, 1, 1);
        if (format == AnimationCompressionFormat.ACF_IntervalFixed32NoW)
        {
            // ACF_IntervalFixed32NoW
            minValue.X = reader.Read<float>();
            minValue.Y = reader.Read<float>();
            minValue.Z = reader.Read<float>();
            rangeValue.X = reader.Read<float>();
            rangeValue.Y = reader.Read<float>();
            rangeValue.Z = reader.Read<float>();
        }

        List<FQuat> values = new List<FQuat>(numKeys);
        switch (format)
        {
            case AnimationCompressionFormat.ACF_None:
                for (int k = 0; k < numKeys; k++)
                {
                    float x = reader.Read<float>();
                    float y = reader.Read<float>();
                    float z = reader.Read<float>();
                    float w = reader.Read<float>();
                    values.Add(new FQuat(x, y, z, w));
                }
                break;
            case AnimationCompressionFormat.ACF_Float96NoW:
                for (int k = 0; k < numKeys; k++)
                {
                    var quat = reader.ReadQuatFloat96NoW();
                    values.Add(quat);
                }
                break;
            case AnimationCompressionFormat.ACF_Fixed48NoW:
                for (int k = 0; k < numKeys; k++)
                {
                    var quat = reader.ReadQuatFixed48NoW(componentMask);
                    values.Add(quat);
                }
                break;
            case AnimationCompressionFormat.ACF_IntervalFixed32NoW:
                for (int k = 0; k < numKeys; k++)
                {
                    var quat = reader.ReadQuatIntervalFixed32NoW(minValue, rangeValue);
                    values.Add(quat);
                }
                break;
            case AnimationCompressionFormat.ACF_Fixed32NoW:
                for (int k = 0; k < numKeys; k++)
                {
                    var quat = reader.ReadQuatFixed32NoW();
                    values.Add(quat);
                }
                break;
        }

        List<float> times = new List<float>(numKeys);
        float frameRate = sequenceLength / (numFrames - 1);

        if (hasTimeTracks)
        {
            // align to 4 bytes
            reader.Position = reader.Position.Align(4);
            float[] dstTimeKeys;
            ReadTimeArray(reader, numKeys, out dstTimeKeys, numFrames);

            for (int k = 0; k < dstTimeKeys.Length; k++)
            {
                times.Add(dstTimeKeys[k] * frameRate);
                frameNumbers.Add((uint)dstTimeKeys[k] + 1); // フレーム番号は1から開始
            }
        }
        else
        {
            // スパースでない場合、連続したフレーム番号を生成
            for (int k = 0; k < numKeys; k++)
            {
                times.Add(k * frameRate);
                frameNumbers.Add((uint)(k + 1)); // フレーム番号は1から開始
            }
        }

        List<(float, FQuat)> result = new List<(float, FQuat)>(numKeys);
        for (int k = 0; k < numKeys; k++)
        {
            result.Add((times[k], values[k]));
        }

        return result;
    }
    private static FVector GetInterpolatedTranslation(List<(float Time, FVector Value)> keys, float time, FVector defaultValue)
    {
        if (keys == null || keys.Count == 0) return defaultValue;
        if (keys.Count == 1) return keys[0].Value;

        int i = 0;
        for (; i < keys.Count - 1; i++)
        {
            if (time < keys[i + 1].Time) break;
        }
        i = Math.Min(i, keys.Count - 2);

        float alpha = (time - keys[i].Time) / (keys[i + 1].Time - keys[i].Time);
        return keys[i].Value + alpha * (keys[i + 1].Value - keys[i].Value);
    }

    private static FQuat GetInterpolatedRotation(List<(float Time, FQuat Value)> keys, float time, FQuat defaultValue)
    {
        if (keys == null || keys.Count == 0) return defaultValue;
        if (keys.Count == 1) return keys[0].Value;

        int i = 0;
        for (; i < keys.Count - 1; i++)
        {
            if (time < keys[i + 1].Time) break;
        }
        i = Math.Min(i, keys.Count - 2);

        float alpha = (time - keys[i].Time) / (keys[i + 1].Time - keys[i].Time);
        return FQuat.Slerp(keys[i].Value, keys[i + 1].Value, alpha);
    }

    private static float InterpolateBlendShapeWeight(FRichCurveKey[] keys, float time)
    {
        if (keys == null || keys.Length == 0) return 0.0f;
        if (keys.Length == 1) return keys[0].Value;

        int i = 0;
        for (; i < keys.Length - 1; i++)
        {
            if (time < keys[i + 1].Time) break;
        }
        i = Math.Min(i, keys.Length - 2);

        float alpha = (time - keys[i].Time) / (keys[i + 1].Time - keys[i].Time);
        return keys[i].Value + alpha * (keys[i + 1].Value - keys[i].Value);
    }

    private static uint TimeToFrame(float time, float secondsPerFrame)
    {
        int frame0based = (int)Math.Round(time / secondsPerFrame);
        return (uint)Math.Max(0, frame0based) + 1;
    }

    private static void ReadTimeArray(FArchive Ar, int numKeys, out float[] times, int numFrames)
    {
        times = new float[numKeys];
        if (numKeys <= 1) return;

        if (numFrames < 256)
        {
            for (var keyIndex = 0; keyIndex < numKeys; keyIndex++)
            {
                var v = Ar.Read<byte>();
                times[keyIndex] = v;
            }
        }
        else
        {
            for (var k = 0; k < numKeys; k++)
            {
                var keyIndex = Ar.Read<ushort>();
                times[k] = keyIndex;
            }
        }

        // align to 4 bytes
        Ar.Position = Ar.Position.Align(4);
    }

    [DllImport(ACLNative.LIB_NAME)]
    private static extern unsafe void nReadACLData(IntPtr compressedTracks, FTransform* inRefPoses, FTrackToSkeletonMap* inTrackToSkeletonMap, FTransform* outAtom);

}