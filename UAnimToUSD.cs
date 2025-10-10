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
        WriteAnimUsd(animFile, sanitizedAnimName, animSequence, skeleton, sourceBoneInfo, usdBones, usedBoneIndices);

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

    private static void WriteAnimUsd(string filePath, string animName, UAnimSequence animSequence, USkeleton skeleton, FMeshBoneInfo[] boneInfo, VtTokenArray usdBones, List<int> usedBoneIndices)
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

            // アニメーションデータを抽出
            ProcessAnimationData(animSequence, skeleton, boneInfo, usdAnim, usedBoneIndices);

            stage.GetRootLayer().Save();
        }
    }
    [DllImport(ACLNative.LIB_NAME)]
    private static extern unsafe void nReadACLData(IntPtr compressedTracks, FTransform* inRefPoses, FTrackToSkeletonMap* inTrackToSkeletonMap, FTransform* outAtom);

    private static void ProcessAnimationData(UAnimSequence animSequence, USkeleton skeleton, FMeshBoneInfo[] boneInfo, UsdSkelAnimation usdAnim, List<int> usedBoneIndices)
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
        var rotationsPerFrame = new VtQuathArray[numFrames];
        var scalesPerFrame = new VtVec3hArray[numFrames];

        for (int f = 0; f < numFrames; f++)
        {
            translationsPerFrame[f] = new VtVec3fArray((uint)numBones);
            rotationsPerFrame[f] = new VtQuathArray((uint)numBones);
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
                        // There could be an animation consisting of only trans with offsets == -1, what means
                        // use of RefPose. In this case there's no point adding the animation to AnimSet. We'll
                        // create FMemReader even for empty CompressedByteStream, otherwise it would be hard to
                        // create a valid CAnimSequence which won't crash animation export.
                        using var reader = new FByteArchive("CompressedByteStream", ueData.CompressedByteStream);
                        for (var boneIndex = 0; boneIndex < numBones; boneIndex++)
                        {
                            //var track = new CAnimTrack();
                            //animSeq.Tracks.Add(track);
                            var trackIndex = animSequence.FindTrackForBoneIndex(boneIndex);
                            if (trackIndex >= 0)
                            {
                                //if (ueData.KeyEncodingFormat == AKF_PerTrackCompression)
                                    //ReadPerTrackData(reader, animSequence, track, trackIndex);
                                //else
                                    //ReadKeyLerpData(reader, animSequence, track, trackIndex, ueData.KeyEncodingFormat == AKF_VariableKeyLerp);
                            }
                        }

                        break;
                    }
                case FACLCompressedAnimData aclData:
                    {
                        var tracks = aclData.GetCompressedTracks();
                        var tracksHeader = tracks.GetTracksHeader();
                        var numSamples = (int)tracksHeader.NumSamples;

                        tracks.SetDefaultScale(0);

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
                                    //rotationsPerFrame[f][boneIndex] = new GfQuath(usdRot.W, usdRot.X, usdRot.Y, usdRot.Z);
                                    //scalesPerFrame[f][boneIndex] = new GfVec3h( new GfHalf(transform.Scale3D.X), new GfHalf(transform.Scale3D.Y), new GfHalf(transform.Scale3D.Z));
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
                                    //rotationsPerFrame[f][(uint)boneIndex] = new GfQuath((float)usdRot.W, (float)usdRot.X, (float)usdRot.Y, (float)usdRot.Z);
                                    //scalesPerFrame[f][(uint)boneIndex] = new GfVec3h(new GfHalf(refPose.Scale3D.X), new GfHalf(refPose.Scale3D.Y), new GfHalf(refPose.Scale3D.Z));
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
        var dict = new VtDictionary();
        //dict.Insert("frameRate", new VtValue(animSequence.RateScale));
        //usdAnim.GetPrim().SetMetadata(new TfToken("customData"), dict);
    }
}

// UAnimSequence拡張（トラックインデックス取得やフレームデータ取得のためのヘルパー）
public static class UAnimSequenceExtensions
{
    public static int GetTrackIndexForBone(this UAnimSequence anim, int boneIndex)
    {
        // 実装例: TrackToSkeletonMapTableからマップ
        // 実際のCUE4Parseの構造に基づいて調整
        // 仮定: anim.TrackToSkeletonMapTable[trackIndex].BoneTreeIndex == boneIndex
        for (int i = 0; i < anim.TrackToSkeletonMapTable.Length; i++)
        {
            if (anim.TrackToSkeletonMapTable[i].BoneTreeIndex == boneIndex)
                return i;
        }
        return -1;
    }

    public static int GetNumTracks(this UAnimSequence anim)
    {
        return anim.TrackToSkeletonMapTable?.Length ?? 0;
    }

    public static int GetTrackBoneIndex(this UAnimSequence anim, int trackIndex)
    {
        return anim.TrackToSkeletonMapTable?[trackIndex].BoneTreeIndex ?? -1;
    }
}