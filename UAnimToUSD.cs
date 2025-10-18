using CommunityToolkit.HighPerformance.Helpers;
using CUE4Parse.ACL;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Animation.ACL;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Readers;
using CUE4Parse.Utils;
using pxr;
using System.Runtime.InteropServices;

public static class UAnimSequenceToUSD
{
    public static void ConvertAnimationToUsd(UAnimSequence animSequence, USkeleton skeleton, string outputDirectory, bool optimizeBones = true)
    {
        if (animSequence == null) throw new ArgumentNullException(nameof(animSequence));
        if (skeleton == null) throw new ArgumentNullException(nameof(skeleton));
        if (string.IsNullOrEmpty(outputDirectory)) throw new ArgumentNullException(nameof(outputDirectory));

        var originalAssetName = skeleton.Name ?? "UnnamedSkeleton";
        var sanitizedPrimName = USkeletalMeshToUSD.SanitizeUsdName(originalAssetName);

        // ルートディレクトリ: outputDirectory/CharacterName/
        var rootDir = Path.Combine(outputDirectory, "Anim", sanitizedPrimName);

        // Animsディレクトリ: outputDirectory/CharacterName/Skel/Anims/
        var animsDir = Path.Combine(rootDir, "Skel", "Anims");
        Directory.CreateDirectory(animsDir);

        // アニメーションファイル名: CharacterName_AnimName_anim.usda
        var animName = animSequence.Name ?? "UnnamedAnimation";
        var sanitizedAnimName = USkeletalMeshToUSD.SanitizeUsdName(animName);
        var animFile = Path.Combine(animsDir, sanitizedAnimName + "_anim.usda");

        // 使用ボーンインデックスを取得（アニメーションから、optimizeBonesに基づく）
        var sourceBoneInfo = skeleton.ReferenceSkeleton.FinalRefBoneInfo;
        var usedBoneIndices = GetUsedBoneIndicesFromAnim(animSequence, sourceBoneInfo, optimizeBones);

        // USDボーンパスを作成
        var usdBones = USkeletalMeshToUSD.BuildUsdBonePaths(sourceBoneInfo, usedBoneIndices);

        // アニメーションUSDを書き出す
        WriteAnimUsd(animFile, sanitizedAnimName, animSequence, skeleton, usdBones, usedBoneIndices);

        Console.WriteLine($"Wrote Animation USD: {animFile}");
    }

    // アニメーションから使用ボーンインデックスを取得（トラックのあるボーンと親をルートまで）
    private static List<int> GetUsedBoneIndicesFromAnim(UAnimSequence animSequence, FMeshBoneInfo[] boneInfo, bool optimizeBones)
    {
        if (!optimizeBones)
        {
            return Enumerable.Range(0, boneInfo.Length).ToList();
        }

        var usedBones = new HashSet<int>();

        // アニメーションの全トラックから使用ボーンを集める
        for (int trackIndex = 0; trackIndex < animSequence.GetNumTracks(); trackIndex++)
        {
            int boneIndex = animSequence.GetTrackBoneIndex(trackIndex);
            if (boneIndex >= 0)
            {
                usedBones.Add(boneIndex);
                // 親をルートまで追加
                int parentIndex = boneInfo[boneIndex].ParentIndex;
                while (parentIndex >= 0)
                {
                    usedBones.Add(parentIndex);
                    parentIndex = boneInfo[parentIndex].ParentIndex;
                }
            }
        }

        return usedBones.OrderBy(x => x).ToList();
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
            // Read time tracks
            float[] dstTimeKeys;
            ReadTimeArray(reader, numKeys, out dstTimeKeys, numFrames);
            Console.WriteLine("hasTimeTracks");
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
                    // 実装が必要な場合
                    // uint packed = reader.Read<uint>();
                    // values.Add(new FQuat(x, y, z, w));
                }
                break;
            case AnimationCompressionFormat.ACF_Fixed32NoW:
                for (int k = 0; k < numKeys; k++)
                {
                    // 実装が必要な場合
                    // values.Add(new FQuat(x, y, z, w));
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
            Console.WriteLine("hasTimeTracks");

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

    private static void ProcessAnimationData(UAnimSequence animSequence, USkeleton skeleton, UsdSkelAnimation usdAnim, List<int> usedBoneIndices, UsdStage stage)
    {
        // アニメーションのフレーム数とレートを取得
        int sequenceNumFrames = animSequence.NumFrames;
        float secondsPerFrame = animSequence.SequenceLength / (sequenceNumFrames - 1); // 秒単位のフレーム間隔
        float floatFps = 1f / secondsPerFrame;
        int fps = (int)Math.Round(floatFps);
        int endFrame = 0;

        // 現フレームの秒数
        var timeCodes = new List<float>(); // Will be populated later based on sparse or dense

        int numBones = usedBoneIndices.Count;

        // translations, rotations, scales の配列を準備（各フレームごとにVtArray）
        VtVec3fArray[] translationsPerFrame = null;
        VtQuatfArray[] rotationsPerFrame = null;
        VtVec3hArray[] scalesPerFrame = null;

        // UAnimSequenceからトラックデータを抽出
        var rawAnimData = animSequence.RawAnimationData;
        if (rawAnimData == null)
        {
            switch (animSequence.CompressedDataStructure)
            {
                case FUECompressedAnimData ueData:
                    {
                        if (ueData.KeyEncodingFormat == AnimationKeyFormat.AKF_ConstantKeyLerp)
                        {
                            // todo
                            return;
                        }
                        if (ueData.KeyEncodingFormat == AnimationKeyFormat.AKF_VariableKeyLerp)
                        {
                            // todo
                            return;
                        }
                        if (ueData.KeyEncodingFormat != AnimationKeyFormat.AKF_PerTrackCompression)
                        {
                            break;
                            throw new NotImplementedException("AnimationKeyFormat.");
                        }

                        using var reader = new FByteArchive("CompressedByteStream", ueData.CompressedByteStream);
                        // CompressedTrackOffsetsからトラック情報を取得
                        var compressedTrackOffsets = ueData.CompressedTrackOffsets;
                        var compressedByteStream = ueData.CompressedByteStream;
                        var compressedScaleOffsets = ueData.CompressedScaleOffsets;

                        // 各トラックのデータをキャッシュ
                        var translationTracksCache = new Dictionary<int, List<(float Time, FVector Value)>>();
                        var rotationTracksCache = new Dictionary<int, List<(float Time, FQuat Value)>>();
                        var scaleTracksCache = new Dictionary<int, List<(float Time, FVector Value)>>();

                        HashSet<uint> uniqueFrameNumbers = new HashSet<uint>();
                        uniqueFrameNumbers.Add(1); // 開始フレームを確保
                        uniqueFrameNumbers.Add((uint)sequenceNumFrames); // 終了フレームを確保

                        // 各トラックを事前に展開
                        var numTracks = animSequence.GetNumTracks();
                        for (int trackIndex = 0; trackIndex < numTracks; trackIndex++)
                        {
                            int boneIndex = animSequence.GetTrackBoneIndex(trackIndex);
                            if (boneIndex < 0) continue;

                            int trackMaxFrameTemp = 0;
                            List<uint> frameNumbers = new List<uint>();

                            // トランスレーション
                            int transOffset = ueData.CompressedTrackOffsets[trackIndex * 2];
                            List<(float, FVector)> translationKeys = null;
                            if (transOffset >= 0)
                            {
                                reader.Position = transOffset;
                                translationKeys = DecompressTranslationTrack(reader, transOffset, animSequence.SequenceLength, sequenceNumFrames, out frameNumbers, out trackMaxFrameTemp);
                                translationTracksCache[boneIndex] = translationKeys;
                                if (frameNumbers != null)
                                {
                                    uniqueFrameNumbers.UnionWith(frameNumbers);
                                }
                            }

                            // ローテーション
                            int rotationOffset = compressedTrackOffsets[trackIndex * 2 + 1];
                            List<(float, FQuat)> rotationKeys = null;
                            if (rotationOffset >= 0)
                            {
                                reader.Position = rotationOffset;
                                rotationKeys = DecompressRotationTrack(reader, rotationOffset, animSequence.SequenceLength, sequenceNumFrames, out frameNumbers, out trackMaxFrameTemp);
                                rotationTracksCache[boneIndex] = rotationKeys;
                                if (frameNumbers != null)
                                {
                                    uniqueFrameNumbers.UnionWith(frameNumbers);
                                }
                            }

                            // スケール
                            if (compressedScaleOffsets != null && trackIndex < compressedScaleOffsets.OffsetData.Length)
                            {
                                int scaleOffset = compressedScaleOffsets.OffsetData[trackIndex];
                                if (scaleOffset != -1 && scaleOffset >= 0 && scaleOffset < compressedByteStream.Count())
                                {
                                    List<(float, FVector)> scaleKeys = null;
                                    reader.Position = scaleOffset;
                                    scaleKeys = DecompressTranslationTrack(reader, scaleOffset, animSequence.SequenceLength, sequenceNumFrames, out frameNumbers, out trackMaxFrameTemp);
                                    scaleTracksCache[boneIndex] = scaleKeys;
                                    if (frameNumbers != null)
                                    {
                                        uniqueFrameNumbers.UnionWith(frameNumbers);
                                    }
                                }
                            }
                        }

                        // ユニークなフレーム番号からtimeCodesを生成
                        var sortedUniqueTimes = uniqueFrameNumbers
                            .Select(frame => (frame - 1) * secondsPerFrame)
                            .OrderBy(time => time)
                            .ToList();

                        timeCodes = sortedUniqueTimes;
                        endFrame = (int)uniqueFrameNumbers.Max();

                        // 配列をtimeCodes.Count分（全フレーム）に確保
                        translationsPerFrame = new VtVec3fArray[timeCodes.Count];
                        rotationsPerFrame = new VtQuatfArray[timeCodes.Count];
                        scalesPerFrame = new VtVec3hArray[timeCodes.Count];

                        for (int f = 0; f < timeCodes.Count; f++)
                        {
                            translationsPerFrame[f] = new VtVec3fArray((uint)numBones);
                            rotationsPerFrame[f] = new VtQuatfArray((uint)numBones);
                            scalesPerFrame[f] = new VtVec3hArray((uint)numBones);
                        }

                        // 各ボーンの各フレームのトランスフォームを計算
                        for (var boneIdx = 0; boneIdx < numBones; boneIdx++)
                        {
                            var boneIndex = usedBoneIndices[boneIdx];
                            var refPose = skeleton.ReferenceSkeleton.FinalRefBonePose[boneIndex];

                            // デフォルト値（リファレンスポーズ）
                            FVector defaultTranslation = refPose.Translation;
                            FQuat defaultRotation = refPose.Rotation;
                            FVector defaultScale = refPose.Scale3D;

                            // このボーンのトラックデータを取得
                            translationTracksCache.TryGetValue(boneIndex, out var translationKeys);
                            rotationTracksCache.TryGetValue(boneIndex, out var rotationKeys);
                            scaleTracksCache.TryGetValue(boneIndex, out var scaleKeys);

                            // 各フレームで補間
                            for (int f = 0; f < timeCodes.Count; f++)
                            {
                                float time = timeCodes[f];

                                // トランスレーション
                                FVector translation = GetInterpolatedTranslation(translationKeys, time, defaultTranslation);

                                // ローテーション
                                FQuat rotation = GetInterpolatedRotation(rotationKeys, time, defaultRotation);

                                // スケール
                                FVector scale = GetInterpolatedTranslation(scaleKeys, time, defaultScale);

                                // UE座標系からUSD座標系に変換
                                var scaledTranslation = translation * USkeletalMeshToUSD.UeToUsdScale;
                                var usdPos = UsdCoordinateTransformer.TransformPosition(scaledTranslation);
                                var usdRot = UsdCoordinateTransformer.TransformRotation(rotation);

                                // USDデータに設定
                                translationsPerFrame[f][boneIdx] = new GfVec3f(usdPos.X, usdPos.Y, usdPos.Z);
                                rotationsPerFrame[f][boneIdx] = new GfQuatf(usdRot.W, usdRot.X, usdRot.Y, usdRot.Z);
                                scalesPerFrame[f][boneIdx] = new GfVec3h(new GfVec3f(scale.X, scale.Y, scale.Z));
                            }
                        }

                        break;
                    }
                case FACLCompressedAnimData aclData:
                    {
                        // 1からnumSamples の全フレームを作成

                        var tracks = aclData.GetCompressedTracks();
                        var tracksHeader = tracks.GetTracksHeader();
                        var numSamples = (int)tracksHeader.NumSamples;

                        // ACLケースでnumFrames, fps, frameRateをtracksHeaderに基づいて上書き
                        int aclNumFrames = numSamples;

                        endFrame = aclNumFrames;
                        fps = (int)tracksHeader.SampleRate;
                        float aclSecondsPerFrame = 1f / tracksHeader.SampleRate;

                        // timeCodesを設定（numSamples分）
                        timeCodes = new List<float>(aclNumFrames);
                        for (int f = 0; f < aclNumFrames; f++)
                        {
                            timeCodes.Add(f * aclSecondsPerFrame);
                        }

                        // 配列をnumFrames (=numSamples)分に再確保
                        translationsPerFrame = new VtVec3fArray[aclNumFrames];
                        rotationsPerFrame = new VtQuatfArray[aclNumFrames];
                        scalesPerFrame = new VtVec3hArray[aclNumFrames];

                        for (int f = 0; f < aclNumFrames; f++)
                        {
                            translationsPerFrame[f] = new VtVec3fArray((uint)numBones);
                            rotationsPerFrame[f] = new VtQuatfArray((uint)numBones);
                            scalesPerFrame[f] = new VtVec3hArray((uint)numBones);
                        }

                        // スケールを1に設定
                        tracks.SetDefaultScale(1);

                        var atomKeys = new FTransform[tracksHeader.NumTracks * aclNumFrames];
                        unsafe
                        {
                            fixed (FTransform* refPosePtr = skeleton.ReferenceSkeleton.FinalRefBonePose)
                            fixed (FTrackToSkeletonMap* trackToSkeletonMapPtr = animSequence.GetTrackMap())
                            fixed (FTransform* atomKeysPtr = atomKeys)
                            {
                                nReadACLData(tracks.Handle, refPosePtr, trackToSkeletonMapPtr, atomKeysPtr);
                            }
                        }

                        for (var boneIndex = 0; boneIndex < numBones; boneIndex++)
                        {
                            var trackIndex = animSequence.FindTrackForBoneIndex(usedBoneIndices[boneIndex]);
                            if (trackIndex >= 0)
                            {
                                var offset = trackIndex * aclNumFrames;
                                for (int f = 0; f < aclNumFrames; f++)
                                {
                                    var transform = atomKeys[offset + f];
                                    var translation = transform.Translation * USkeletalMeshToUSD.UeToUsdScale;
                                    var usdPos = UsdCoordinateTransformer.TransformPosition(translation);
                                    var usdRot = UsdCoordinateTransformer.TransformRotation(transform.Rotation);

                                    translationsPerFrame[f][boneIndex] = new GfVec3f(usdPos.X, usdPos.Y, usdPos.Z);
                                    rotationsPerFrame[f][boneIndex] = new GfQuatf(usdRot.W, usdRot.X, usdRot.Y, usdRot.Z);

                                    var gfVec3F = new GfVec3f(transform.Scale3D.X, transform.Scale3D.Y, transform.Scale3D.Z);
                                    // Cast GfVec3 GfVec3h
                                    scalesPerFrame[f][boneIndex] = new GfVec3h(gfVec3F);
                                }
                            }
                            else
                            {
                                // Use reference pose for bones without animation tracks
                                var refPose = skeleton.ReferenceSkeleton.FinalRefBonePose[usedBoneIndices[boneIndex]];
                                var translation = refPose.Translation * USkeletalMeshToUSD.UeToUsdScale;
                                var usdPos = UsdCoordinateTransformer.TransformPosition(translation);
                                var usdRot = UsdCoordinateTransformer.TransformRotation(refPose.Rotation);

                                for (int f = 0; f < aclNumFrames; f++)
                                {
                                    translationsPerFrame[f][boneIndex] = new GfVec3f(usdPos.X, usdPos.Y, usdPos.Z);
                                    rotationsPerFrame[f][boneIndex] = new GfQuatf(usdRot.W, usdRot.X, usdRot.Y, usdRot.Z);

                                    var gfVec3F = new GfVec3f(refPose.Scale3D.X, refPose.Scale3D.Y, refPose.Scale3D.Z);
                                    // Cast GfVec3 GfVec3h
                                    scalesPerFrame[f][boneIndex] = new GfVec3h(gfVec3F);
                                }
                            }
                        }
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
        // 各ボーン（usedBoneIndices順）ごとにトラックを処理


        // 1からendFrameまでのtimeCodesを設定（スパースまたは密）
        var startFrame = TimeToFrame(timeCodes.First(), secondsPerFrame);
        stage.SetStartTimeCode(startFrame);
        stage.SetEndTimeCode(endFrame);

        // timeCodesPerSecond
        stage.SetTimeCodesPerSecond(fps);  // 30 FPSなど
        // framesPerSecond
        stage.SetFramesPerSecond(fps);


        // timeSamplesを設定
        var translationsAttr = usdAnim.CreateTranslationsAttr();
        var rotationsAttr = usdAnim.CreateRotationsAttr();
        var scalesAttr = usdAnim.CreateScalesAttr();

        // スパースフレームに対応
        for (int f = 0; f < timeCodes.Count; f++)
        {
            double time = TimeToFrame(timeCodes[f], secondsPerFrame); // フレーム時間からフレーム番号を取得（1から始まる）
            translationsAttr.Set(translationsPerFrame[f], new UsdTimeCode(time));
        }

        for (int f = 0; f < timeCodes.Count; f++)
        {
            double time = TimeToFrame(timeCodes[f], secondsPerFrame); // フレーム時間からフレーム番号を取得（1から始まる）
            rotationsAttr.Set(rotationsPerFrame[f], new UsdTimeCode(time));
        }

        for (int f = 0; f < timeCodes.Count; f++)
        {
            double time = TimeToFrame(timeCodes[f], secondsPerFrame); // フレーム時間からフレーム番号を取得（1から始まる）
            if (scalesPerFrame.Length > f)
                scalesAttr.Set(scalesPerFrame[f], new UsdTimeCode(time));
        }

        // todo Blendshape
        if (animSequence.CompressedCurveByteStream.Length > 0)
        {
            var blendShapesName = animSequence.CompressedCurveNames;
            var blendShapes = animSequence.CompressedCurveData;
            var codec = animSequence.CurveCompressionCodec;

            return;
        }
        
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