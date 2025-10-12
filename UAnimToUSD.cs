using CommunityToolkit.HighPerformance.Helpers;
using CUE4Parse.ACL;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Animation.ACL;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Readers;
using pxr;
using System.Runtime.InteropServices;
using static CUE4Parse.UE4.Objects.Core.i18N.FTextHistory;

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
            ProcessAnimationData(animSequence, skeleton, usdAnim, usedBoneIndices);

            stage.GetRootLayer().Save();
        }
    }
    [DllImport(ACLNative.LIB_NAME)]
    private static extern unsafe void nReadACLData(IntPtr compressedTracks, FTransform* inRefPoses, FTrackToSkeletonMap* inTrackToSkeletonMap, FTransform* outAtom);

    private static void ProcessAnimationData(UAnimSequence animSequence, USkeleton skeleton, UsdSkelAnimation usdAnim, List<int> usedBoneIndices)
    {
        // アニメーションのフレーム数とレートを取得
        int numFrames = animSequence.NumFrames;
        float frameRate = animSequence.SequenceLength / (numFrames - 1); // 秒単位のフレーム間隔
        var timeCodes = new VtFloatArray((uint)numFrames);
        for (int f = 0; f < numFrames; f++)
        {
            timeCodes[f] = f * frameRate; // timeSamplesのキーとして使用（秒単位）
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

        // UAnimSequenceからトラックデータを抽出
        var rawAnimData = animSequence.RawAnimationData;
        if (rawAnimData == null)
        {
            switch (animSequence.CompressedDataStructure)
            {
                case FUECompressedAnimData ueData:
                    {
                        break;
                        using var reader = new FByteArchive("CompressedByteStream", ueData.CompressedByteStream);
                        var numSamples = numFrames;
                        var numTracks = animSequence.GetNumTracks();
                        var trackToSkeleton = animSequence.CompressedTrackToSkeletonMapTable;
                        var atomKeys = new FTransform[numTracks * numSamples];
                        var sequenceLength = animSequence.SequenceLength;

                        // 1. 全トラックのキーフレームを解凍
                        for (var trackIndex = 0; trackIndex < numTracks; trackIndex++)
                        {
                            int transOffset = ueData.CompressedTrackOffsets[trackIndex * 2];
                            int rotOffset = ueData.CompressedTrackOffsets[trackIndex * 2 + 1];
                            int scaleOffset =-1;
                            if (ueData.CompressedScaleOffsets.OffsetData.Length > 0)
                            {
                                scaleOffset = ueData.CompressedScaleOffsets.GetOffsetData(trackIndex, -1);
                            }

                            var (transTimes, transKeys) = DecompressVectorTrack(reader, transOffset, numFrames, sequenceLength, isScale: false);
                            var (rotTimes, rotKeys) = DecompressRotationTrack(reader, rotOffset, numFrames, sequenceLength);
                            var (scaleTimes, scaleKeys) = DecompressVectorTrack(reader, scaleOffset, numFrames, sequenceLength, isScale: true);

                            var outputOffset = trackIndex * numSamples;

                            // 2. 全フレームを補間
                            for (int f = 0; f < numSamples; f++)
                            {
                                var time = (f == 0 || numFrames <= 1) ? 0.0f : (float)f / (numFrames - 1) * sequenceLength;

                                var translation = InterpolateVector(transTimes, transKeys, time, FVector.ZeroVector);
                                var rotation = InterpolateRotation(rotTimes, rotKeys, time);
                                var scale = InterpolateVector(scaleTimes, scaleKeys, time, FVector.OneVector);

                                atomKeys[outputOffset + f] = new FTransform(rotation, translation, scale);
                            }
                        }

                        // --- ここから下は元のコードと同じ ---
                        // Now, similar to ACL, assign to perFrame arrays
                        for (var boneIndex = 0; boneIndex < numBones; boneIndex++)
                        {
                            var skeletonBoneIndex = usedBoneIndices[boneIndex];
                            var trackIndex = animSequence.FindTrackForBoneIndex(skeletonBoneIndex);
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
                                    scalesPerFrame[f][boneIndex] = new GfVec3h(gfVec3F);
                                }
                            }
                        }

                        break;
                    }
                case FACLCompressedAnimData aclData:
                    {
                        var tracks = aclData.GetCompressedTracks();
                        var tracksHeader = tracks.GetTracksHeader();
                        var numSamples = (int)tracksHeader.NumSamples;

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

        // 各ボーン（usedBoneIndices順）ごとにトラックを処理

        // timeSamplesを設定
        var translationsAttr = usdAnim.CreateTranslationsAttr();
        var rotationsAttr = usdAnim.CreateRotationsAttr();
        var scalesAttr = usdAnim.CreateScalesAttr();

        for (int f = 0; f < numFrames; f++)
        {
            double time = timeCodes[f];
            translationsAttr.Set(translationsPerFrame[f], new UsdTimeCode(time));
            rotationsAttr.Set(rotationsPerFrame[f], new UsdTimeCode(time));
            scalesAttr.Set(scalesPerFrame[f], new UsdTimeCode(time));
        }

        // frameRateを設定（metadata）
        //var dict = new VtDictionary();
        //dict.insert("frameRate", new VtValue(animSequence.RateScale));
        //usdAnim.GetPrim().SetMetadata(new TfToken("customData"), dict);
    }

    // --- 以下, ヘルパーメソッド群をクラス内に追加 ---

    // CUE4ParseにはFVectorやFQuatのLerp/Slerpがない場合を想定した簡易的な実装
    private static FVector Lerp(FVector a, FVector b, float t) => a + (b - a) * t;
    private static FQuat Slerp(FQuat a, FQuat b, float t)
    {
        // CUE4ParseのFQuatにSlerpがない場合の簡易実装
        // 実際にはドット product をチェックするなど、より堅牢な実装が必要
        var result = a * (1.0f - t) + b * t;
        result.Normalize();
        return result;
    }

    // キーフレームリストから指定時間における値を補間して取得
    private static FVector InterpolateVector(List<float> times, List<FVector> keys, float time, FVector defaultValue)
    {
        if (keys == null || keys.Count == 0) return defaultValue;
        if (keys.Count == 1) return keys[0];

        // 補間すべきキーフレームの区間を探す
        int index1 = times.FindLastIndex(t => t <= time);
        if (index1 < 0) index1 = 0;
        int index2 = Math.Min(index1 + 1, keys.Count - 1);

        if (index1 == index2) return keys[index1];

        // 2つのキーフレーム間で線形補間
        float time1 = times[index1];
        float time2 = times[index2];
        float alpha = (time - time1) / (time2 - time1);

        return Lerp(keys[index1], keys[index2], alpha);
    }

    private static FQuat InterpolateRotation(List<float> times, List<FQuat> keys, float time)
    {
        if (keys == null || keys.Count == 0) return FQuat.Identity;
        if (keys.Count == 1) return keys[0];

        int index1 = times.FindLastIndex(t => t <= time);
        if (index1 < 0) index1 = 0;
        int index2 = Math.Min(index1 + 1, keys.Count - 1);

        if (index1 == index2) return keys[index1];

        float time1 = times[index1];
        float time2 = times[index2];
        float alpha = (time - time1) / (time2 - time1);

        return Slerp(keys[index1], keys[index2], alpha);
    }

    // 位置(Vector)またはスケール(Vector)トラックを解凍する
    private static (List<float> KeyTimes, List<FVector> KeyValues) DecompressVectorTrack(FByteArchive reader, int offset, int numFrames, float sequenceLength, bool isScale = false)
    {
        if (offset == -1) return (null, null);

        reader.Seek(offset, SeekOrigin.Begin);
        var packedInfo = reader.Read<uint>();
        var numKeys = (int)(packedInfo & 0x00FFFFFFu);
        var keyFormat = (AnimationCompressionFormat)(packedInfo >> 24);

        if (numKeys == 0) return (null, null);

        var keyValues = new List<FVector>(numKeys);

        switch (keyFormat)
        {
            case AnimationCompressionFormat.ACF_None:
            case AnimationCompressionFormat.ACF_Float96NoW:
                for (int i = 0; i < numKeys; i++) keyValues.Add(reader.Read<FVector>());
                break;
            case AnimationCompressionFormat.ACF_Fixed48NoW:
                for (int i = 0; i < numKeys; i++)
                {
                    ushort x = reader.Read<ushort>();
                    ushort y = reader.Read<ushort>();
                    ushort z = reader.Read<ushort>();
                    int A = (x & 0x7FF) - 1024;
                    int B = (y & 0x7FF) - 1024;
                    int C = (z & 0x7FF) - 1024;
                    float invLen = 1f / 2048f;
                    keyValues.Add(new FVector(A * invLen, B * invLen, C * invLen));
                }
                break;
            case AnimationCompressionFormat.ACF_IntervalFixed32NoW:
                var mins = reader.Read<FVector>();
                var ranges = reader.Read<FVector>();
                for (int i = 0; i < numKeys; i++)
                {
                    uint packed = reader.Read<uint>();
                    float x = ((packed & 0x3FF) / 1023f) * ranges.X + mins.X;
                    float y = (((packed >> 10) & 0x3FF) / 1023f) * ranges.Y + mins.Y;
                    float z = (((packed >> 20) & 0x3FF) / 1023f) * ranges.Z + mins.Z;
                    keyValues.Add(new FVector(x, y, z));
                }
                break;
            case AnimationCompressionFormat.ACF_Identity:
                keyValues.Add(isScale ? FVector.OneVector : FVector.ZeroVector);
                break;
            case (AnimationCompressionFormat)17:
            case (AnimationCompressionFormat)18:
            case (AnimationCompressionFormat)19:
            case (AnimationCompressionFormat)21:
            case (AnimationCompressionFormat)23:
            case (AnimationCompressionFormat)35:
            case (AnimationCompressionFormat)39:
            case (AnimationCompressionFormat)55:
                // Assuming this is a half-float format (3 * 16 bits = 48 bits)
                for (int i = 0; i < numKeys; i++)
                {
                    Half x = reader.Read<Half>();
                    Half y = reader.Read<Half>();
                    Half z = reader.Read<Half>();
                    keyValues.Add(new FVector((float)x, (float)y, (float)z));
                }
                break;
            default:
                for (int i = 0; i < numKeys; i++)
                {
                    Half x = reader.Read<Half>();
                    Half y = reader.Read<Half>();
                    Half z = reader.Read<Half>();
                    keyValues.Add(new FVector((float)x, (float)y, (float)z));
                } 
                break;
                //throw new NotImplementedException($"Unsupported vector format {keyFormat}");
        }

        var keyTimes = new List<float>(numKeys);
        for (int i = 0; i < numKeys; i++)
        {
            keyTimes.Add((numKeys <= 1) ? 0.0f : (float)i / (numKeys - 1) * sequenceLength);
        }

        return (keyTimes, keyValues);
    }

    // 回転(Quaternion)トラックを解凍する
    private static (List<float> KeyTimes, List<FQuat> KeyValues) DecompressRotationTrack(FByteArchive reader, int offset, int numFrames, float sequenceLength)
    {
        if (offset == -1) return (null, null);

        reader.Seek(offset, SeekOrigin.Begin);
        var packedInfo = reader.Read<uint>();
        var numKeys = (int)(packedInfo & 0x00FFFFFFu);
        var keyFormat = (AnimationCompressionFormat)(packedInfo >> 24);

        if (numKeys == 0) return (null, null);

        var keyValues = new List<FQuat>(numKeys);

        switch (keyFormat)
        {
            case AnimationCompressionFormat.ACF_None:
                for (int i = 0; i < numKeys; i++) keyValues.Add(reader.Read<FQuat>());
                break;
            case AnimationCompressionFormat.ACF_Float96NoW:
                for (int i = 0; i < numKeys; i++)
                {
                    FVector v = reader.Read<FVector>();
                    float wSq = Math.Max(1f - (v.X * v.X + v.Y * v.Y + v.Z * v.Z), 0f);
                    float w = (float)Math.Sqrt(wSq);
                    keyValues.Add(new FQuat(v.X, v.Y, v.Z, w));
                }
                break;
            case AnimationCompressionFormat.ACF_Fixed48NoW:
                for (int i = 0; i < numKeys; i++)
                {
                    ushort M = reader.Read<ushort>();
                    ushort N = reader.Read<ushort>();
                    ushort O = reader.Read<ushort>();
                    int A = (M & 0x7FF) - 1024;
                    int B = (N & 0x7FF) - 1024;
                    int C = (O & 0x7FF) - 1024;
                    float sumSquare = A * A + B * B + C * C;
                    float wSq = Math.Max(2048 * 2048 - sumSquare, 0f);
                    float w = (float)Math.Sqrt(wSq);
                    if ((M & 0x8000) != 0 || (N & 0x8000) != 0 || (O & 0x8000) != 0) w = -w;
                    float invLen = 1f / 2048f;
                    var quat = new FQuat(A * invLen, B * invLen, C * invLen, w * invLen);
                    quat.Normalize();
                    keyValues.Add(quat);
                }
                break;
            case AnimationCompressionFormat.ACF_IntervalFixed32NoW:
                var mins = reader.Read<FVector>();
                var ranges = reader.Read<FVector>();
                for (int i = 0; i < numKeys; i++)
                {
                    uint packed = reader.Read<uint>();
                    float x = ((packed & 0x3FF) / 1023f) * ranges.X + mins.X;
                    float y = (((packed >> 10) & 0x3FF) / 1023f) * ranges.Y + mins.Y;
                    float z = (((packed >> 20) & 0x3FF) / 1023f) * ranges.Z + mins.Z;
                    float wSq = 1f - (x * x + y * y + z * z);
                    float w = (wSq > 0f) ? (float)Math.Sqrt(wSq) : 0f;
                    var quat = new FQuat(x, y, z, w);
                    quat.Normalize();
                    keyValues.Add(quat);
                }
                break;
            case AnimationCompressionFormat.ACF_Identity:
                keyValues.Add(FQuat.Identity);
                break;
            case (AnimationCompressionFormat)23:
            case (AnimationCompressionFormat)31:
            case (AnimationCompressionFormat)34:
            case (AnimationCompressionFormat)36:
            case (AnimationCompressionFormat)38:
            case (AnimationCompressionFormat)39:
            case (AnimationCompressionFormat)47:
                // Assuming half-float format similar to ACF_Float96NoW
                for (int i = 0; i < numKeys; i++)
                {
                    Half hx = reader.Read<Half>();
                    Half hy = reader.Read<Half>();
                    Half hz = reader.Read<Half>();
                    float x = (float)hx;
                    float y = (float)hy;
                    float z = (float)hz;
                    float wSq = Math.Max(1f - (x * x + y * y + z * z), 0f);
                    float w = (float)Math.Sqrt(wSq);
                    var quat = new FQuat(x, y, z, w);
                    quat.Normalize();
                    keyValues.Add(quat);
                }
                break;
            default:
                throw new NotImplementedException($"Unsupported rotation format {keyFormat}");
        }

        var keyTimes = new List<float>(numKeys);
        for (int i = 0; i < numKeys; i++)
        {
            keyTimes.Add((numKeys <= 1) ? 0.0f : (float)i / (numKeys - 1) * sequenceLength);
        }

        return (keyTimes, keyValues);
    }
}