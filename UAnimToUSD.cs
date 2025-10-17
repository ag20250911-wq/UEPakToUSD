using CommunityToolkit.HighPerformance.Helpers;
using CUE4Parse.ACL;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Animation.ACL;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Readers;
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
        var animFile = Path.Combine(animsDir, sanitizedPrimName + "_" + sanitizedAnimName + "_anim.usda");

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

            // アニメーションデータを抽出
            ProcessAnimationData(animSequence, skeleton, usdAnim, usedBoneIndices, stage);

            stage.GetRootLayer().Save();
        }
    }
    [DllImport(ACLNative.LIB_NAME)]
    private static extern unsafe void nReadACLData(IntPtr compressedTracks, FTransform* inRefPoses, FTrackToSkeletonMap* inTrackToSkeletonMap, FTransform* outAtom);

    private static List<(float Time, FVector Value)> DecompressTranslationTrack(List<byte> item, float sequenceLength, int numFrames)
    {
        if (item == null) return null;

        byte[] data = item.ToArray();

        int pos = 0;

        uint packedInfo = BitConverter.ToUInt32(data, pos); pos += 4;

        AnimationCompressionFormat format = (AnimationCompressionFormat)(packedInfo >> 28);

        int componentMask = (int)((packedInfo >> 24) & 0xF);

        int numKeys = (int)(packedInfo & 0xFFFFFF);

        bool hasTimeTracks = (componentMask & 8) != 0;

        if (numKeys == 0) return null;

        // サポート外フォーマットチェック
        if (format == AnimationCompressionFormat.ACF_Fixed48NoW || format == AnimationCompressionFormat.ACF_Fixed32NoW || format == AnimationCompressionFormat.ACF_Float32NoW)
        {
            throw new NotSupportedException("This format is not supported for translation tracks.");
        }

        int keySize = 0;

        int preKeySize = 0;

        switch (format)
        {
            case AnimationCompressionFormat.ACF_None:
            case AnimationCompressionFormat.ACF_Float96NoW:
                keySize = 12;
                break;
            case AnimationCompressionFormat.ACF_IntervalFixed32NoW:
                keySize = 4;
                preKeySize = 24;
                break;
            case AnimationCompressionFormat.ACF_Identity:
                return new List<(float, FVector)> { (0, new FVector(0, 0, 0)) };
            default:
                throw new NotImplementedException($"Unsupported format for translation: {format}");
        }

        int keyDataSize = numKeys * keySize;

        int timeDataSize = 0;

        int timeByteSize = 0;

        if (hasTimeTracks)
        {
            timeDataSize = data.Length - pos - preKeySize - keyDataSize;
            if (timeDataSize <= 0 || timeDataSize % numKeys != 0) throw new InvalidDataException("Invalid time data size.");
            timeByteSize = timeDataSize / numKeys;
            if (timeByteSize != 1 && timeByteSize != 2 && timeByteSize != 4) throw new InvalidDataException("Invalid time byte size.");
        }

        float frameRate = sequenceLength / (numFrames - 1);

        List<float> times = new List<float>(numKeys);

        if (hasTimeTracks)
        {
            for (int k = 0; k < numKeys; k++)
            {
                uint frame = 0;
                if (timeByteSize == 1)
                {
                    frame = data[pos++];
                }
                else if (timeByteSize == 2)
                {
                    frame = BitConverter.ToUInt16(data, pos);
                    pos += 2;
                }
                else
                {
                    frame = BitConverter.ToUInt32(data, pos);
                    pos += 4;
                }
                times.Add(frame * frameRate);
            }
        }
        else
        {
            float keyInterval = sequenceLength / (numKeys - 1);
            for (int k = 0; k < numKeys; k++)
            {
                times.Add(k * keyInterval);
            }
        }

        FVector minValue = new FVector(0, 0, 0);
        FVector rangeValue = new FVector(1, 1, 1);
        if (format == AnimationCompressionFormat.ACF_IntervalFixed32NoW)
        {
            minValue.X = BitConverter.ToSingle(data, pos); pos += 4;
            minValue.Y = BitConverter.ToSingle(data, pos); pos += 4;
            minValue.Z = BitConverter.ToSingle(data, pos); pos += 4;
            rangeValue.X = BitConverter.ToSingle(data, pos); pos += 4;
            rangeValue.Y = BitConverter.ToSingle(data, pos); pos += 4;
            rangeValue.Z = BitConverter.ToSingle(data, pos); pos += 4;
        }

        List<FVector> values = new List<FVector>(numKeys);
        switch (format)
        {
            case AnimationCompressionFormat.ACF_None:
            case AnimationCompressionFormat.ACF_Float96NoW:
                for (int k = 0; k < numKeys; k++)
                {
                    float x = 0, y = 0, z = 0;

                    if ((componentMask & 1) != 0)
                    {
                        if (pos + 4 > data.Length) throw new InvalidDataException("Insufficient data for X component.");
                        x = BitConverter.ToSingle(data, pos); pos += 4;
                    }

                    if ((componentMask & 2) != 0)
                    {
                        if (pos + 4 > data.Length) throw new InvalidDataException("Insufficient data for Y component.");
                        y = BitConverter.ToSingle(data, pos); pos += 4;
                    }

                    if ((componentMask & 4) != 0)
                    {
                        if (pos + 4 > data.Length) throw new InvalidDataException("Insufficient data for Z component.");
                        z = BitConverter.ToSingle(data, pos); pos += 4;
                    }

                    values.Add(new FVector(x, y, z));
                }
                break;
            case AnimationCompressionFormat.ACF_IntervalFixed32NoW:
                float xScale = rangeValue.X / 2047.0f;
                float yScale = rangeValue.Y / 2047.0f;
                float zScale = rangeValue.Z / 1023.0f;
                for (int k = 0; k < numKeys; k++)
                {
                    uint packed = BitConverter.ToUInt32(data, pos); pos += 4;
                    float x = ((packed >> 21) & 0x7FF) * xScale + minValue.X;
                    float y = ((packed >> 10) & 0x7FF) * yScale + minValue.Y;
                    float z = (packed & 0x3FF) * zScale + minValue.Z;
                    values.Add(new FVector(x, y, z));
                }
                break;
        }

        List<(float, FVector)> result = new List<(float, FVector)>(numKeys);
        for (int k = 0; k < numKeys; k++)
        {
            result.Add((times[k], values[k]));
        }
        return result;
    }

    private static List<(float Time, FQuat Value)> DecompressRotationTrack(FArchive reader, int offsetIndex, float sequenceLength, int numFrames)
    {
        if (reader == null) return null;
        
        int pos = 0;
        uint packedInfo = reader.Read<UInt32>(); pos += 4;

        AnimationCompressionFormat format = (AnimationCompressionFormat)(packedInfo >> 28);

        int componentMask = (int)((packedInfo >> 24) & 0xF);

        int numKeys = (int)(packedInfo & 0xFFFFFF);

        bool hasTimeTracks = (componentMask & 8) != 0;

        if (numKeys == 0) return null;

        int keySize = 0;

        int preKeySize = 0;

        switch (format)
        {
            case AnimationCompressionFormat.ACF_None:
                keySize = 16;
                break;
            case AnimationCompressionFormat.ACF_Float96NoW:
                keySize = 12;
                break;
            case AnimationCompressionFormat.ACF_Fixed48NoW:
                keySize = 6;
                break;
            case AnimationCompressionFormat.ACF_IntervalFixed32NoW:
                keySize = 4;
                preKeySize = 24;
                break;
            case AnimationCompressionFormat.ACF_Fixed32NoW:
                keySize = 4;
                break;
            case AnimationCompressionFormat.ACF_Float32NoW:
                throw new NotImplementedException("ACF_Float32NoW decompression not implemented.");
            case AnimationCompressionFormat.ACF_Identity:
                return new List<(float, FQuat)> { (0, FQuat.Identity) };
            default:
                throw new NotImplementedException($"Unsupported format for rotation: {format}");
        }

        int keyDataSize = numKeys * keySize;

        int timeDataSize = 0;

        int timeByteSize = 0;

        if (hasTimeTracks)
        {
            timeDataSize = (int)reader.Length - (int)offsetIndex - pos - preKeySize - keyDataSize;
            if (timeDataSize <= 0 || timeDataSize % numKeys != 0) throw new InvalidDataException("Invalid time data size.");
            timeByteSize = timeDataSize / numKeys;
            if (timeByteSize != 1 && timeByteSize != 2 && timeByteSize != 4) throw new InvalidDataException("Invalid time byte size.");
        }

        float frameRate = sequenceLength / (numFrames - 1);

        List<float> times = new List<float>(numKeys);

        if (hasTimeTracks)
        {
            for (int k = 0; k < numKeys; k++)
            {
                uint frame = 0;
                if (timeByteSize == 1)
                {
                    //frame = data[pos++];
                    pos++;
                }
                else if (timeByteSize == 2)
                {
                    //frame = BitConverter.ToUInt16(data, pos);
                    pos += 2;
                }
                else
                {
                    //frame = BitConverter.ToUInt32(data, pos);
                    pos += 4;
                }
                times.Add(frame * frameRate);
            }
        }
        else
        {
            float keyInterval = sequenceLength / (numKeys - 1);
            for (int k = 0; k < numKeys; k++)
            {
                times.Add(k * keyInterval);
            }
        }

        FVector minValue = new FVector(0, 0, 0);
        FVector rangeValue = new FVector(1, 1, 1);
        if (format == AnimationCompressionFormat.ACF_IntervalFixed32NoW)
        {
            //minValue.X = BitConverter.ToSingle(data, pos); pos += 4;
            //minValue.Y = BitConverter.ToSingle(data, pos); pos += 4;
            //minValue.Z = BitConverter.ToSingle(data, pos); pos += 4;
            //rangeValue.X = BitConverter.ToSingle(data, pos); pos += 4;
            //rangeValue.Y = BitConverter.ToSingle(data, pos); pos += 4;
            //rangeValue.Z = BitConverter.ToSingle(data, pos); pos += 4;
            int test = 0;
        }

        List<FQuat> values = new List<FQuat>(numKeys);
        float sqrt2_2 = (float)Math.Sqrt(2.0) / 2.0f;
        switch (format)
        {
            case AnimationCompressionFormat.ACF_None:
                for (int k = 0; k < numKeys; k++)
                {
                    //float x = BitConverter.ToSingle(data, pos); pos += 4;
                    //float y = BitConverter.ToSingle(data, pos); pos += 4;
                    //float z = BitConverter.ToSingle(data, pos); pos += 4;
                    //float w = BitConverter.ToSingle(data, pos); pos += 4;
                    //values.Add(new FQuat(x, y, z, w));
                }
                break;
            case AnimationCompressionFormat.ACF_Float96NoW:
                for (int k = 0; k < numKeys; k++)
                {
                    //float x = BitConverter.ToSingle(data, pos); pos += 4;
                    //float y = BitConverter.ToSingle(data, pos); pos += 4;
                    //float z = BitConverter.ToSingle(data, pos); pos += 4;
                    //float w = (float)Math.Sqrt(1.0f - (x * x + y * y + z * z));
                    //values.Add(new FQuat(x, y, z, w));
                }
                break;
            case AnimationCompressionFormat.ACF_Fixed48NoW:
                for (int k = 0; k < numKeys; k++)
                {
                    var quat = reader.ReadQuatFixed48NoW(componentMask);
                    values.Add(quat);
                    pos += 4;
                }
                break;
            case AnimationCompressionFormat.ACF_IntervalFixed32NoW:
                for (int k = 0; k < numKeys; k++)
                {
                    //uint packed = BitConverter.ToUInt32(data, pos); pos += 4;
                    //values.Add(new FQuat(x, y, z, w));
                }
                break;
            case AnimationCompressionFormat.ACF_Fixed32NoW:
                for (int k = 0; k < numKeys; k++)
                {
                    //values.Add(new FQuat(x, y, z, w));
                }
                break;
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
        int numFrames = animSequence.NumFrames;
        float frameRate = animSequence.SequenceLength / (numFrames - 1); // 秒単位のフレーム間隔
        float floatFps = 1f / frameRate;
        int fps = (int)Math.Round(floatFps); // 30 FPSなど

        var timeCodes = new VtFloatArray((uint)numFrames);
        for (int f = 0; f < numFrames; f++)
        {
            timeCodes[f] = f + 1;  // フレーム番号を指定 1から（UsdTimeCodeのデフォルト単位）
        }

        int numBones = usedBoneIndices.Count;

        // translations, rotations, scales の配列を準備（各フレームごとにVtArray）
        var translationsPerFrame = new VtVec3fArray[numFrames];
        var rotationsPerFrame = new VtQuatfArray[numFrames];
        var scalesPerFrame = new VtVec3hArray[numFrames];

        for (int f = 0; f < numFrames; f++)
        {
            translationsPerFrame[f] = new VtVec3fArray((uint)numBones);
            rotationsPerFrame[f] = new VtQuatfArray((uint)numBones);
            scalesPerFrame[f] = new VtVec3hArray((uint)numBones);
        }

        // 1からnumFramesまでのtimeCodesを設定
        stage.SetStartTimeCode(1);
        stage.SetEndTimeCode(numFrames);

        // timeCodesPerSecond
        stage.SetTimeCodesPerSecond(fps);  // 30 FPSを設定
        // framesPerSecond
        stage.SetFramesPerSecond(fps);


        // UAnimSequenceからトラックデータを抽出
        var rawAnimData = animSequence.RawAnimationData;
        if (rawAnimData == null)
        {
            switch (animSequence.CompressedDataStructure)
            {
                case FUECompressedAnimData ueData:
                    {
                        if (ueData.KeyEncodingFormat != AnimationKeyFormat.AKF_PerTrackCompression)
                        {
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


                        // 全トラックを事前に展開
                        var numTracks = animSequence.GetNumTracks();
                        for (int trackIndex = 0; trackIndex < numTracks; trackIndex++)
                        {
                            int boneIndex = animSequence.GetTrackBoneIndex(trackIndex);
                            if (boneIndex < 0) continue;

                            // Translationトラックの展開
                            var transOffset = trackIndex * 2;
                            if (transOffset < compressedTrackOffsets.Length &&
                                compressedTrackOffsets[transOffset] != -1)
                            {
                                int offsetIndex = compressedTrackOffsets[transOffset];
                                if (offsetIndex >= 0 && offsetIndex < compressedByteStream.Count())
                                {
                                    var trackData = compressedByteStream[offsetIndex..].ToList<byte>();
                                    try
                                    {
                                        var translationKeys = DecompressTranslationTrack(
                                            trackData,
                                            animSequence.SequenceLength,
                                            numFrames);

                                        if (translationKeys != null && translationKeys.Count > 0)
                                        {
                                            translationTracksCache[boneIndex] = translationKeys;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"Warning: Failed to decompress translation track {transOffset} for bone {boneIndex}: {ex.Message}");
                                    }
                                }
                            }

                            // Rotationトラックの展開
                            int rotationOffset = trackIndex * 2 + 1;
                            if (rotationOffset < compressedTrackOffsets.Length &&
                                compressedTrackOffsets[rotationOffset] != -1)
                            {
                                int offsetIndex = compressedTrackOffsets[rotationOffset];
                                // Positionを設定
                                reader.Position = offsetIndex;

                                if (offsetIndex >= 0 && offsetIndex < compressedByteStream.Count())
                                {
                                    try
                                    {
                                        var rotationKeys = DecompressRotationTrack(
                                            reader,
                                            offsetIndex,
                                            animSequence.SequenceLength,
                                            numFrames);

                                        if (rotationKeys != null && rotationKeys.Count > 0)
                                        {
                                            rotationTracksCache[boneIndex] = rotationKeys;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"Warning: Failed to decompress rotation track {trackIndex} for bone {boneIndex}: {ex.Message}");
                                    }
                                }
                            }

                            // Scaleトラックの展開（存在する場合）
                            if (compressedScaleOffsets != null &&
                                trackIndex < compressedScaleOffsets.OffsetData.Length)
                            {
                                int scaleOffsetIndex = compressedScaleOffsets.OffsetData[trackIndex];
                                if (scaleOffsetIndex != -1 &&
                                    scaleOffsetIndex >= 0 &&
                                    scaleOffsetIndex < compressedByteStream.Count())
                                {
                                    var trackData = compressedByteStream[scaleOffsetIndex..].ToList<byte>();
                                    try
                                    {
                                        var scaleKeys = DecompressTranslationTrack(
                                            trackData,
                                            animSequence.SequenceLength,
                                            numFrames);

                                        if (scaleKeys != null && scaleKeys.Count > 0)
                                        {
                                            scaleTracksCache[boneIndex] = scaleKeys;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"Warning: Failed to decompress scale track {trackIndex} for bone {boneIndex}: {ex.Message}");
                                    }
                                }
                            }
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
                            for (int f = 0; f < numFrames; f++)
                            {
                                float time = f * frameRate;

                                // Translation
                                FVector translation = GetInterpolatedTranslation(
                                    translationKeys,
                                    time,
                                    defaultTranslation);

                                // Rotation
                                FQuat rotation = GetInterpolatedRotation(
                                    rotationKeys,
                                    time,
                                    defaultRotation);

                                // Scale
                                FVector scale = GetInterpolatedTranslation(
                                    scaleKeys,
                                    time,
                                    defaultScale);

                                // UE座標系からUSD座標系に変換
                                var scaledTranslation = translation * USkeletalMeshToUSD.UeToUsdScale;
                                var usdPos = UsdCoordinateTransformer.TransformPosition(scaledTranslation);
                                var usdRot = UsdCoordinateTransformer.TransformRotation(rotation);

                                // USDデータに設定
                                translationsPerFrame[f][boneIdx] = new GfVec3f(usdPos.X, usdPos.Y, usdPos.Z);
                                rotationsPerFrame[f][boneIdx] = new GfQuatf(usdRot.W, usdRot.X, usdRot.Y, usdRot.Z);

                                var gfVec3F = new GfVec3f(scale.X, scale.Y, scale.Z);
                                scalesPerFrame[f][boneIdx] = new GfVec3h(gfVec3F);
                            }
                        }

                        break;
                    }
                case FACLCompressedAnimData aclData:
                    {
                        var tracks = aclData.GetCompressedTracks();
                        var tracksHeader = tracks.GetTracksHeader();
                        var numSamples = (int)tracksHeader.NumSamples;

                        // ここでフレームレートを設定（tracksHeader.SampleRateを使用）
                        // timeCodesPerSecond
                        //stage.SetTimeCodesPerSecond(tracksHeader.SampleRate);  // 30 FPSを設定
                        // framesPerSecond
                        //stage.SetFramesPerSecond(tracksHeader.SampleRate);

                        // 1からnumSamplesまでのtimeCodesを設定
                        //stage.SetStartTimeCode(1);
                        //stage.SetEndTimeCode(numSamples);

                        // スケールを1に設定
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

                        for (var boneIndex = 0; boneIndex < numBones; boneIndex++)
                        {
                            var trackIndex = animSequence.FindTrackForBoneIndex(usedBoneIndices[boneIndex]);
                            if (trackIndex >= 0)
                            {
                                var offset = trackIndex * numSamples;
                                for (int f = 0; f < Math.Min(numSamples, numFrames); f++)
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

                                for (int f = 0; f < numFrames; f++)
                                {
                                    translationsPerFrame[f][boneIndex] = new GfVec3f(usdPos.X, usdPos.Y, usdPos.Z);
                                    rotationsPerFrame[f][boneIndex] = new GfQuatf(usdRot.W, usdRot.X, usdRot.Y, usdRot.Z);

                                    var gfVec3F = new GfVec3f(refPose.Scale3D.X, refPose.Scale3D.Y, refPose.Scale3D.Z);
                                    // Cast GfVec3 GfVec3h
                                    scalesPerFrame[f][boneIndex] = new GfVec3h(gfVec3F);
                                }
                            }
                            continue;
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

        // timeSamplesを設定
        var translationsAttr = usdAnim.CreateTranslationsAttr();
        var rotationsAttr = usdAnim.CreateRotationsAttr();
        var scalesAttr = usdAnim.CreateScalesAttr();

        for (int f = 0; f < numFrames; f++)
        {
            double time = timeCodes[f]; // フレーム番号をそのまま使用（1から始まる）
            translationsAttr.Set(translationsPerFrame[f], new UsdTimeCode(time));
            rotationsAttr.Set(rotationsPerFrame[f], new UsdTimeCode(time));
            scalesAttr.Set(scalesPerFrame[f], new UsdTimeCode(time));
        }

    }


    
}