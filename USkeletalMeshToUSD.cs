using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.RenderCore;
using CUE4Parse.UE4.Objects.UObject;
using pxr;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using static System.Collections.Specialized.BitVector32;

public static class USkeletalMeshToUSD
{
    private const string OriginalNameAttribute = "UEOriginalName";
    private const string OriginalMaterialSlotNameAttribute = "UEOriginalMaterialSlotName";
    public static string ScopePath { get; set; } = "/Geo";
    public static string ScopeSkeletonPath { get; set; } = "/Skeleton";
    public static string ScopeSkeletonsXformPath { get; set; } = "/SkeletonsXform";

    public static string SanitizeUsdName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "Unnamed";
        }

        var sanitizedBuilder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sanitizedBuilder.Append(c);
            }
            else
            {
                sanitizedBuilder.Append('_');
            }
        }

        if (sanitizedBuilder.Length > 0 && char.IsDigit(sanitizedBuilder[0]))
        {
            sanitizedBuilder.Insert(0, '_');
        }

        return sanitizedBuilder.ToString();
    }

    /// <summary>
    /// 分割してUSDを出力するメイン（Geo, Skeleton, Root の3ファイルを作る）
    /// </summary>
    public static void ConvertToSplitUsd(USkeletalMesh skeletalMesh, string outputDirectory, bool optimizeBones = true)
    {
        if (skeletalMesh == null) throw new ArgumentNullException(nameof(skeletalMesh));
        if (string.IsNullOrEmpty(outputDirectory)) throw new ArgumentNullException(nameof(outputDirectory));
        Directory.CreateDirectory(outputDirectory);

        var originalAssetName = skeletalMesh.Name ?? "UnnamedSkeletalMesh";
        var sanitizedPrimName = SanitizeUsdName(originalAssetName);

        // ファイル名
        var geoFile = Path.Combine(outputDirectory, sanitizedPrimName + "_geo.usda");
        var skeletonFile = Path.Combine(outputDirectory, sanitizedPrimName + "_skeleton.usda");
        var rootFile = Path.Combine(outputDirectory, sanitizedPrimName + "_root.usda");

        // LODチェック
        if (skeletalMesh.LODModels == null || skeletalMesh.LODModels.Length == 0) return;
        var lodModel = skeletalMesh.LODModels[0];
        if (lodModel.VertexBufferGPUSkin?.VertsFloat == null || lodModel.Indices == null) return;

        var sourceVertices = lodModel.VertexBufferGPUSkin.VertsFloat;
        var sourceIndices = GetSourceIndices(lodModel.Indices);
        if (sourceVertices.Length == 0 || sourceIndices.Length == 0) return;
        var sourceBoneInfo = skeletalMesh.ReferenceSkeleton.FinalRefBoneInfo;
        var sourceBoneIndexMap = skeletalMesh.ReferenceSkeleton.FinalNameToIndexMap;

        // 最適化されたボーンリストを作成（optimizeBonesがtrueの場合）
        List<int> usedBoneIndices = GetUsedBoneIndices(lodModel.Sections, sourceBoneInfo, optimizeBones);

        // 頂点/法線・スキニングデータを作成（メモリ表現）
        ProcessVerticesAndNormals(sourceVertices, out VtVec3fArray usdVertices, out VtVec3fArray usdNormals);

        uint maxElementSize = 0;
        foreach (var section in lodModel.Sections)
        {
            maxElementSize = Math.Max(maxElementSize, (uint)section.MaxBoneInfluences);
        }
        uint elementSize = maxElementSize;


        int numBones = skeletalMesh.ReferenceSkeleton.FinalRefBoneInfo.Length;
        ProcessSkinningData(sourceVertices, lodModel.Sections, out VtFloatArray usdBoneWeights, out VtIntArray usdBoneIndices, elementSize, usedBoneIndices, numBones, optimizeBones);

        var usdBones = BuildUsdBonePaths(sourceBoneInfo, usedBoneIndices);

        // 1) Geo ファイル作成（ジオメトリ + スキン属性を含める。ただし Skeleton 参照はここでは付けない）
        WriteGeoUsd(geoFile, sanitizedPrimName, originalAssetName, usdVertices, usdNormals, sourceIndices, usdBoneWeights, usdBoneIndices, usdBones, elementSize, skeletalMesh);

        // 2) Skeleton ファイル作成（スケルトン / ジョイントXform）
        WriteSkeletonUsd(skeletonFile, sanitizedPrimName, skeletalMesh, sourceBoneInfo, usdBones, usedBoneIndices);

        // 3) Root ファイル作成（UsdSkelRoot を作り、geo/skeleton を reference で取り込む。さらに mesh -> skeleton の binding を作る）
        WriteRootUsd(rootFile, sanitizedPrimName, geoFile, skeletonFile);

        return;
    }

    // 使用されているボーンインデックスを取得（ウェイトがあるボーンとその親をルートまで）
    private static List<int> GetUsedBoneIndices(FSkelMeshSection[] sections, FMeshBoneInfo[] boneInfo, bool optimizeBones)
    {
        if (!optimizeBones)
        {
            // 全てのボーンを使用
            return Enumerable.Range(0, boneInfo.Length).ToList();
        }

        var usedBones = new HashSet<int>();

        // 各セクションのBoneMapから使用ボーンを集める
        foreach (var section in sections)
        {
            var boneMap = section.BoneMap;
            foreach (var localBoneIndex in boneMap)
            {
                int globalBoneIndex = localBoneIndex;
                // ウェイトがあるボーンを追加
                usedBones.Add(globalBoneIndex);
                // 親をルートまで追加
                int parentIndex = boneInfo[globalBoneIndex].ParentIndex;
                while (parentIndex >= 0)
                {
                    usedBones.Add(parentIndex);
                    parentIndex = boneInfo[parentIndex].ParentIndex;
                }
            }
        }

        // ソートしてリスト化（元のインデックス順に）
        return usedBones.OrderBy(x => x).ToList();
    }

    // Geo用USDを書き出す（メッシュとスキンのattrsを持つが、Skeleton参照は持たない）
    private static void WriteGeoUsd(string filePath, string primName, string originalAssetName, VtVec3fArray points, VtVec3fArray normals, int[] indices, VtFloatArray jointWeights, VtIntArray jointIndices, VtTokenArray usdBones, uint elementSize, USkeletalMesh skeletalMesh)
    {
        using (var stage = UsdStage.CreateNew(filePath))
        {
            if (stage == null) throw new InvalidOperationException($"Failed to create Geo USD: {filePath}");

            // 普通は /Geo をルートにする
            UsdGeom.UsdGeomSetStageUpAxis(stage, UsdGeomTokens.y);
            UsdGeom.UsdGeomSetStageMetersPerUnit(stage, 1);

            var geoScope = UsdGeomScope.Define(stage, new SdfPath(ScopePath)); // e.g. /Geo
            stage.SetDefaultPrim(geoScope.GetPrim());

            var meshPath = geoScope.GetPath().AppendChild(new TfToken(primName));
            var usdMesh = UsdGeomMesh.Define(stage, meshPath);
            usdMesh.GetPrim().CreateAttribute(new TfToken(OriginalNameAttribute), SdfValueTypeNames.String).Set(originalAssetName);
            usdMesh.CreateOrientationAttr().Set(UsdGeomTokens.rightHanded);
            usdMesh.CreateSubdivisionSchemeAttr().Set(new TfToken("none"));

            // geometry attributes
            usdMesh.CreatePointsAttr().Set(points);
            usdMesh.CreateNormalsAttr().Set(normals);
            usdMesh.SetNormalsInterpolation(UsdGeomTokens.vertex);

            var faceVertexIndices = new VtIntArray((uint)indices.Length);
            for (int i = 0; i < indices.Length; i++) faceVertexIndices[i] = indices[i];
            usdMesh.CreateFaceVertexIndicesAttr().Set(faceVertexIndices);
            uint triCount = (uint)(indices.Length / 3);
            var faceVertexCounts = new VtIntArray(triCount);
            for (int i = 0; i < triCount; i++) faceVertexCounts[i] = 3;
            usdMesh.CreateFaceVertexCountsAttr().Set(faceVertexCounts);

            // スキニング属性（jointIndices / jointWeights）を頂点インターポレーションで付与
            // UsdSkelBinding は Root 側で行う予定だが、indices/weights自体はメッシュ側に含めておく
            var prim = usdMesh.GetPrim();

            var usdSkin = UsdSkelBindingAPI.Apply(usdMesh.GetPrim());
            // jointWeights
            var jointWeightsAttr = usdSkin.CreateJointWeightsAttr(jointWeights);
            jointWeightsAttr.SetMetadata(new TfToken("elementSize"), elementSize);
            jointWeightsAttr.SetMetadata(new TfToken("interpolation"), UsdGeomTokens.vertex);
            // jointIndices
            var jointIndicesAttr = usdSkin.CreateJointIndicesAttr(jointIndices);
            jointIndicesAttr.SetMetadata(new TfToken("elementSize"), elementSize);
            jointIndicesAttr.SetMetadata(new TfToken("interpolation"), UsdGeomTokens.vertex);

            // Jointのパス 一応持っておく
            usdSkin.CreateJointsAttr(usdBones);


            // BlendShape 存在する場合はプリム作成
            BlendShapeProcessor.ProcessBlendShapes(skeletalMesh, usdMesh);

            var lodModel = skeletalMesh.LODModels[0];
            var outputDirectory = Path.GetDirectoryName(filePath);




            //ProcessSubsetsAndMaterials(usdMesh, lodModel, skeletalMesh, outputDirectory);
            // --- サブセット（マテリアルごとのグループ）とマテリアルの処理 ---
            try
            {
                var meshSections = lodModel.Sections ?? Array.Empty<FSkelMeshSection>();
                var skeletalMaterials = skeletalMesh.SkeletalMaterials ?? Array.Empty<FSkeletalMaterial>();
                var materialInterfaces = skeletalMesh.Materials ?? Array.Empty<ResolvedObject>();


                // GeomSubset を作成する
                int triangleOffset = 0;
                for (int sectionIndex = 0; sectionIndex < meshSections.Length; sectionIndex++)
                {
                    var section = meshSections[sectionIndex];

                    var materialSlotName = skeletalMaterials.ElementAtOrDefault(section.MaterialIndex)?.MaterialSlotName.Text ?? $"mat_{section.MaterialIndex}";


                    var sanitizedSubsetName = SanitizeUsdName(materialSlotName);
                    var subsetFaceIndices = new VtIntArray();


                    // 注意: ここで push しているのは「三角形インデックス」（face index）であり、
                    // faceVertexIndices（頂点インデックスのフラット配列）とは異なる。
                    for (int t = 0; t < section.NumTriangles; t++)
                    {
                        subsetFaceIndices.push_back(triangleOffset + t);
                    }
                    triangleOffset += (int)section.NumTriangles;


                    var subset = UsdGeomSubset.CreateGeomSubset(
                    usdMesh,
                    new TfToken(sanitizedSubsetName),
                    new TfToken(UsdGeomTokens.face),
                    subsetFaceIndices,
                    new TfToken("materialBind") // material:binding を目的としたファミリー
                    );
                    subset.GetPrim().CreateAttribute(new TfToken(OriginalMaterialSlotNameAttribute), Sdf.SdfGetValueTypeString()).Set(materialSlotName);


                    // マテリアルが存在すればテクスチャをエクスポート
                    if (section.MaterialIndex >= 0 && section.MaterialIndex < materialInterfaces.Length)
                    {
                        try
                        {
                            var materialObject = materialInterfaces[section.MaterialIndex];
                            if (materialObject != null && materialObject.TryLoad(out UObject loadedMaterialObject) && loadedMaterialObject is UMaterialInstanceConstant materialInstance)
                            {
                                UsdTextureExporter.ExportMaterialTextures(materialInstance, outputDirectory);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Warning: Failed to process material at index {section.MaterialIndex}. {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // サブセット処理中のエラーは無視し、処理を続行
                Console.WriteLine($"Warning: Failed during subset creation. {ex.Message}");
            }
            stage.GetRootLayer().Save();
        }
    }

    // Skeleton用USDを書き出す（UsdSkelSkeleton とジョイントXform群を出力）
    private static void WriteSkeletonUsd(string filePath, string primName, USkeletalMesh skeletalMesh, FMeshBoneInfo[] boneInfo, VtTokenArray usdBones, List<int> usedBoneIndices)
    {
        using (var stage = UsdStage.CreateNew(filePath))
        {
            if (stage == null) throw new InvalidOperationException($"Failed to create Skeleton USD: {filePath}");

            UsdGeom.UsdGeomSetStageUpAxis(stage, UsdGeomTokens.y);
            UsdGeom.UsdGeomSetStageMetersPerUnit(stage, 1);

            // Skeleton scope
            //var skelScope = UsdSkelRoot.Define(stage, new SdfPath(ScopeSkeletonPath)); // ここでは /Skeleton をルートにする
            var skelScope = UsdGeomScope.Define(stage, new SdfPath(ScopeSkeletonPath)); // ここでは /Skeleton をルートにする
            stage.SetDefaultPrim(skelScope.GetPrim());

            // ジョイントXform群のスコープ
            var skelXformScope = UsdGeomScope.Define(stage, new SdfPath(ScopeSkeletonsXformPath));

            // Skeleton prim path: /Skeleton/<primName>
            var skeletonName = SanitizeUsdName(skeletalMesh.Name ?? "Skeleton");
            var skeletonPath = skelScope.GetPath().AppendChild(new TfToken(skeletonName));
            var usdSkeleton = UsdSkelSkeleton.Define(stage, skeletonPath);

            int numBones = usedBoneIndices.Count;
            var restArray = new VtMatrix4dArray((uint)numBones);
            var bindArray = new VtMatrix4dArray((uint)numBones);
            var jointNames = new VtTokenArray((uint)numBones);

            var localMatrices = new GfMatrix4d[numBones];
            var worldMatrices = new GfMatrix4d[numBones];

            var usdXforms = new List<UsdGeomXform>();

            for (int i = 0; i < numBones; i++)
            {
                int originalIndex = usedBoneIndices[i];
                var item = skeletalMesh.ReferenceSkeleton.FinalRefBonePose[originalIndex];
                var uePos = item.Translation * 0.01f;
                var transformedPos = UsdCoordinateTransformer.TransformPosition(uePos);
                var ueRot = item.Rotation;
                var transformedRot = UsdCoordinateTransformer.TransformRotation(ueRot);
                var q_usd = new GfQuatd(transformedRot.W, transformedRot.X, transformedRot.Y, transformedRot.Z);

                var localM = new GfMatrix4d(1).SetRotate(q_usd);
                localM.SetTranslateOnly(new GfVec3d(transformedPos.X, transformedPos.Y, transformedPos.Z));
                localMatrices[i] = localM;

                // joint names
                jointNames[i] = new TfToken(skeletalMesh.ReferenceSkeleton.FinalRefBoneInfo[originalIndex].Name.Text);

                // create xform under /SkeletonsXform/<jointPath>
                var pathBuilder = new StringBuilder();
                int curr = originalIndex;
                while (curr >= 0)
                {
                    pathBuilder.Insert(0, "/" + SanitizeUsdName(skeletalMesh.ReferenceSkeleton.FinalRefBoneInfo[curr].Name.Text));
                    curr = skeletalMesh.ReferenceSkeleton.FinalRefBoneInfo[curr].ParentIndex;
                }
                var jointRelPath = pathBuilder.ToString().TrimStart('/');
                var jointFullPath = new SdfPath(ScopeSkeletonsXformPath).AppendPath(new SdfPath(jointRelPath));
                var usdXform = UsdGeomXform.Define(stage, jointFullPath);
                usdXform.AddTransformOp().Set(localM);
                usdXforms.Add(usdXform);

                //UsdGeomCube.Define(stage, usdXform.GetPath().AppendChild(new TfToken("Cube"))).GetSizeAttr().Set(0.05);
            }


            var xformCacheAPI = new UsdGeomXformCache();
            for (int i = 0; i < numBones; i++)
            {
                var originalIndex = usedBoneIndices[i];
                var parentIdx = boneInfo[originalIndex].ParentIndex;
                if (parentIdx < 0) worldMatrices[i] = localMatrices[i];
                else
                {
                    var xformPrim = usdXforms[i];
                    worldMatrices[i] = xformCacheAPI.GetLocalToWorldTransform(xformPrim.GetPrim());
                }
            }

            for (int i = 0; i < numBones; i++)
            {
                bindArray[i] = worldMatrices[i];
                restArray[i] = localMatrices[i];
            }

            usdSkeleton.CreateBindTransformsAttr().Set(bindArray);
            usdSkeleton.CreateRestTransformsAttr().Set(restArray);
            usdSkeleton.CreateJointsAttr().Set(usdBones);
            usdSkeleton.CreateJointNamesAttr().Set(jointNames);

            stage.GetRootLayer().Save();
        }
    }

    // Root USD を作り、geo/skeleton を reference で取り込む（結果として一つの SkelRoot 配下に統合される）
    private static void WriteRootUsd(string filePath, string primName, string geoFilePath, string skeletonFilePath)
    {
        using (var stage = UsdStage.CreateNew(filePath))
        {
            if (stage == null) throw new InvalidOperationException($"Failed to create Root USD: {filePath}");

            UsdGeom.UsdGeomSetStageUpAxis(stage, UsdGeomTokens.y);
            UsdGeom.UsdGeomSetStageMetersPerUnit(stage, 1);

            // ここで /SkelRoot を作成（名前は自由）
            var skelRootPath = new SdfPath("/SkelRoot");
            var skelRoot = UsdSkelRoot.Define(stage, skelRootPath);
            stage.SetDefaultPrim(skelRoot.GetPrim());

            // /SkelRoot/Geo を作り、それに geoFile を reference でマッピング
            var geoTargetPath = skelRootPath.AppendPath(new SdfPath(ScopePath.TrimStart('/'))); // /SkelRoot/Geo
            var geoPrim = stage.DefinePrim(geoTargetPath);
            // 参照を追加： geoFile を参照して、その内部にある /Geo をここにマップ
            // Sdf.Reference の呼び出し方はバインディング依存なので必要に応じて修正してください
            geoPrim.GetReferences().AddReference(new SdfReference(Path.GetFileName(geoFilePath), new SdfPath(ScopePath)));

            // 同様に /SkelRoot/Skeleton を作り、skeletonFile を reference
            var skeletonTargetPath = skelRootPath.AppendPath(new SdfPath(ScopeSkeletonPath.TrimStart('/'))); // /SkelRoot/Skeleton
            var skeletonPrim = stage.DefinePrim(skeletonTargetPath);
            skeletonPrim.GetReferences().AddReference(new SdfReference(Path.GetFileName(skeletonFilePath), new SdfPath(ScopeSkeletonPath)));

            // 最後に、Geo 内の Mesh と Skeleton の間に binding を作る（ここでは prim path を仮定）
            // 例: /SkelRoot/Geo/<primName> がメッシュ、/SkelRoot/Skeleton/<skeletonName> がスケルトン
            var meshPrimPath = geoTargetPath.AppendChild(new TfToken(primName)); // /SkelRoot/Geo/<primName>
            var skeletonName = SanitizeUsdName(primName); // ここはユーザーの命名規則に合わせて調整
            var skeletonPrimPath = skeletonTargetPath.AppendChild(new TfToken(skeletonName)); // /SkelRoot/Skeleton/<skeletonName>

            var meshPrim = stage.GetPrimAtPath(meshPrimPath);
            if (meshPrim.IsValid())
            {
                // UsdSkelBindingAPI を用いて skeleton のリレーションを作成
                var bindingApi = UsdSkelBindingAPI.Apply(meshPrim);
                bindingApi.CreateSkeletonRel().AddTarget(skeletonPrimPath);
            }
            else
            {
                Console.WriteLine($"Warning: mesh prim {meshPrimPath} not present in composed stage at this time. Binding deferred.");
            }

            stage.GetRootLayer().Save();
        }

        // 重要: root.usda に記述した reference は相対パスとして機能するように、
        // geoFilePath と skeletonFilePath を同じディレクトリに置いてください（ここではファイル名のみを参照に使っています）。
    }

    private static int[] GetSourceIndices(FMultisizeIndexContainer indices)
    {
        if (indices.Indices16 != null && indices.Indices16.Length > 0)
        {
            return Array.ConvertAll(indices.Indices16, i => (int)i);
        }
        else if (indices.Indices32 != null && indices.Indices32.Length > 0)
        {
            return indices.Indices32.Select(i => (int)i).ToArray();
        }
        return Array.Empty<int>();
    }

    private static void ProcessVerticesAndNormals(FGPUVertFloat[] sourceVertices, out VtVec3fArray usdVertices, out VtVec3fArray usdNormals)
    {
        usdVertices = new VtVec3fArray((uint)sourceVertices.Length);
        usdNormals = new VtVec3fArray((uint)sourceVertices.Length);

        for (int i = 0; i < sourceVertices.Length; i++)
        {
            var vertex = sourceVertices[i];
            var uePos = vertex.Pos * 0.01f; // Scale to meters
            var ueNormal = vertex.Normal[2]; // 3つ目が法線

            var transformedPos = UsdCoordinateTransformer.TransformPosition(uePos);
            var transformedNormal = UsdCoordinateTransformer.TransformNormal(ueNormal);

            usdVertices[i] = new GfVec3f(transformedPos.X, transformedPos.Y, transformedPos.Z);
            usdNormals[i] = new GfVec3f(transformedNormal.X, transformedNormal.Y, transformedNormal.Z);
        }
    }

    private static void ProcessSkinningData(FGPUVertFloat[] sourceVertices, FSkelMeshSection[] sections, out VtFloatArray usdBoneWeights, out VtIntArray usdBoneIndices, uint elementSize, List<int> usedBoneIndices, int numBones, bool optimizeBones)
    {
        usdBoneWeights = new VtFloatArray((uint)sourceVertices.Length * elementSize);
        usdBoneIndices = new VtIntArray((uint)sourceVertices.Length * elementSize);

        // 最適化時用のリマップ辞書（元のグローバルインデックス -> 新しいインデックス）
        Dictionary<int, int> remapBoneIndex = new Dictionary<int, int>();
        if (optimizeBones)
        {
            for (int i = 0; i < usedBoneIndices.Count; i++)
            {
                remapBoneIndex[usedBoneIndices[i]] = i;
            }
        }

        int flatIndex = 0;
        foreach (var section in sections)
        {
            // 使われてるBone のリスト
            var boneMap = section.BoneMap;
            int startVertex = (int)section.BaseVertexIndex;
            int numVertices = (int)section.NumVertices;
            uint sectionInfluences = (uint)section.MaxBoneInfluences;

            for (int v = 0; v < numVertices; v++)
            {
                var vertex = sourceVertices[startVertex + v];

                for (int i = 0; i < sectionInfluences; i++)
                {
                    usdBoneWeights[flatIndex] = vertex.Infs.BoneWeight[i] / 255.0f;
                    int localBoneIndex = vertex.Infs.BoneIndex[i];
                    // データが不正
                    if (localBoneIndex >= boneMap.Length)
                    {
                        usdBoneIndices[flatIndex] = 0;
                        usdBoneWeights[flatIndex] = 0;
                        flatIndex++;
                        Console.WriteLine($"vertex.Infs.BoneIndex[{i}] -> '{localBoneIndex}': 不正なデータ");
                        continue;
                    }

                    int globalBoneIndex = boneMap[localBoneIndex]; // sourceBoneIndexMapのインデックスに相当するグローバルインデックスにマッピング

                    // データが不正
                    if (globalBoneIndex >= numBones)
                    {
                        usdBoneIndices[flatIndex] = 0;
                        usdBoneWeights[flatIndex] = 0;
                        flatIndex++;
                        Console.WriteLine($"vertex.Infs.BoneIndex[{i}] -> '{localBoneIndex}': 不正なデータ");
                        continue;
                    }

                    // 最適化時はリマップされたインデックスを使う
                    if (optimizeBones && remapBoneIndex.TryGetValue(globalBoneIndex, out int remappedIndex))
                    {
                        usdBoneIndices[flatIndex] = remappedIndex;
                    }
                    else
                    {
                        usdBoneIndices[flatIndex] = globalBoneIndex;
                    }

                    if (usdBoneWeights[flatIndex] == 0)
                        usdBoneIndices[flatIndex] = 0;
                    flatIndex++;
                }

                // パディング: sectionInfluences < elementSize の場合、0で埋める
                for (int i = (int)sectionInfluences; i < elementSize; i++)
                {
                    usdBoneWeights[flatIndex] = 0.0f;
                    usdBoneIndices[flatIndex] = 0; // ウェイト0なのでインデックスは任意（0でOK）
                    flatIndex++;
                }
            }
        }
    }

    private static VtTokenArray BuildUsdBonePaths(FMeshBoneInfo[] boneInfo, List<int> usedBoneIndices)
    {
        var usdBones = new VtTokenArray((uint)usedBoneIndices.Count);
        var bonePaths = new List<string>(usedBoneIndices.Count);

        for (int i = 0; i < usedBoneIndices.Count; i++)
        {
            int originalIndex = usedBoneIndices[i];
            var pathBuilder = new StringBuilder();
            int currentIndex = originalIndex;
            while (currentIndex >= 0)
            {
                var boneName = boneInfo[currentIndex].Name;
                pathBuilder.Insert(0, "/" + SanitizeUsdName(boneName.Text));
                currentIndex = boneInfo[currentIndex].ParentIndex;
            }
            bonePaths.Add(pathBuilder.ToString().TrimStart('/')); // Remove leading '/' for relative path
        }

        for (int i = 0; i < bonePaths.Count; i++)
        {
            usdBones[i] = new TfToken(bonePaths[i]);
        }

        return usdBones;
    }

}

public static class UsdCoordinateTransformer
{
    public static Vector3 TransformPosition(FVector uePos)
    {
        // UE to USD mapping: USD(x, y, z) = UE(y, z, -x)
        float usdX = uePos.Y;
        float usdY = uePos.Z;
        float usdZ = -uePos.X; // 左手座標系から右手座標系に

        // -90° Y rotation: x' = -z, z' = x
        float temp = usdX;
        usdX = -usdZ; // x' = -(-uePos.X) = uePos.X
        usdZ = temp;  // z' = uePos.Y

        return new Vector3(usdX, usdY, usdZ);
    }

    public static Quaternion TransformRotation(FQuat ueRot)
    {
        var q_ue = new Quaternion(ueRot.X, ueRot.Y, ueRot.Z, ueRot.W);
        var r_ue = Matrix4x4.CreateFromQuaternion(q_ue);

        var T = new Matrix4x4(
            0, 1, 0, 0,
            0, 0, 1, 0,
            -1, 0, 0, 0,
            0, 0, 0, 1
        );

        var R_mat = new Matrix4x4(
            0, 0, -1, 0,
            0, 1, 0, 0,
            1, 0, 0, 0,
            0, 0, 0, 1
        );

        var M = Matrix4x4.Multiply(R_mat, T);

        if (!Matrix4x4.Invert(M, out var invM))
        {
            throw new InvalidOperationException("Cannot invert coordinate transformation matrix.");
        }

        var r_usd = Matrix4x4.Multiply(M, Matrix4x4.Multiply(r_ue, invM));
        var q_usd = Quaternion.CreateFromRotationMatrix(r_usd);
        return Quaternion.Normalize(q_usd);
    }

    // FPackedNormal用の公開メソッド
    public static Vector3 TransformNormal(FPackedNormal ueNormal)
    {
        // 共通ロジックを呼び出す
        return TransformNormalLogic(ueNormal.X, ueNormal.Y, ueNormal.Z);
    }

    // FVector用の公開メソッド
    public static Vector3 TransformNormal(FVector ueNormal)
    {
        // 共通ロジックを呼び出す
        return TransformNormalLogic(ueNormal.X, ueNormal.Y, ueNormal.Z);
    }

    private static Vector3 TransformNormalLogic(float ueNormalX, float ueNormalY, float ueNormalZ)
    {
        // UE to USD mapping: USD(x, y, z) = UE(y, z, -x)
        float nX = ueNormalY;
        float nY = ueNormalZ;
        float nZ = -ueNormalX; // 左手座標系から右手座標系に

        // -90° Y rotation: x' = -z, z' = x
        float nTemp = nX;
        nX = -nZ;
        nZ = nTemp;

        return Vector3.Normalize(new Vector3(nX, nY, nZ));
    }
}

public static class UsdTextureExporter
{
    public static void ExportMaterialTextures(UMaterialInstanceConstant materialInstance, string outputDirectory)
    {
        if (materialInstance == null) return;

        var materialDirectoryPath = Path.Combine(outputDirectory, USkeletalMeshToUSD.SanitizeUsdName(materialInstance.Name ?? "Material"));
        Directory.CreateDirectory(materialDirectoryPath);

        var decoder = new BcDecoder();

        foreach (var textureParameter in materialInstance.TextureParameterValues ?? Enumerable.Empty<FTextureParameterValue>())
        {
            try
            {
                var resolvedObject = textureParameter.ParameterValue?.ResolvedObject;
                if (resolvedObject == null) continue;

                if (!resolvedObject.TryLoad(out UObject textureObject) || textureObject is not UTexture2D texture2D)
                {
                    continue;
                }

                if (texture2D.PlatformData?.VTData != null)
                    continue; // Skip virtual textures

                var mipMap = texture2D.GetFirstMip();
                if (mipMap?.BulkData?.Data == null || mipMap.SizeX == 0 || mipMap.SizeY == 0) continue;

                var compressedData = mipMap.BulkData.Data;
                int width = mipMap.SizeX;
                int height = mipMap.SizeY;

                byte[] decodedPixelData = DecodeTextureData(decoder, texture2D, compressedData, width, height);

                if (decodedPixelData == null) continue;

                if (texture2D.IsTextureCube) continue;

                var outputFilePath = Path.Combine(materialDirectoryPath, texture2D.Name) + (texture2D.IsHDR ? ".exr" : ".png");

                SaveTextureImage(decodedPixelData, width, height, texture2D.IsHDR, outputFilePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to export a texture parameter. {ex.Message}");
            }
        }
    }

    private static byte[] DecodeTextureData(BcDecoder decoder, UTexture2D texture2D, byte[] compressedData, int width, int height)
    {
        byte[] decodedPixelData = null;

        switch (texture2D.Format)
        {
            case EPixelFormat.PF_B8G8R8A8:
            case EPixelFormat.PF_G8:
            case EPixelFormat.PF_FloatRGBA when texture2D.IsHDR:
                decodedPixelData = compressedData;
                break;
            case EPixelFormat.PF_DXT1:
                decodedPixelData = DecodeToBgra(decoder, compressedData, width, height, CompressionFormat.Bc1);
                break;
            case EPixelFormat.PF_DXT5:
                decodedPixelData = DecodeToBgra(decoder, compressedData, width, height, CompressionFormat.Bc3);
                break;
            case EPixelFormat.PF_BC5:
                decodedPixelData = DecodeToBgra(decoder, compressedData, width, height, CompressionFormat.Bc5);
                if (texture2D.IsNormalMap && decodedPixelData != null)
                {
                    ReconstructNormalMapZChannel(decodedPixelData, width, height);
                }
                break;
            case EPixelFormat.PF_BC7:
                decodedPixelData = DecodeToBgra(decoder, compressedData, width, height, CompressionFormat.Bc7);
                break;
            default:
                return null;
        }

        return decodedPixelData;
    }

    private static void SaveTextureImage(byte[] decodedPixelData, int width, int height, bool isHDR, string outputFilePath)
    {
        try
        {
            if (isHDR)
            {
                using (var image = Image.LoadPixelData<RgbaVector>(decodedPixelData, width, height))
                {
                    image.SaveAsPng(outputFilePath);
                }
            }
            else
            {
                using (var image = Image.LoadPixelData<Bgra32>(decodedPixelData, width, height))
                {
                    image.SaveAsPng(outputFilePath);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save texture to '{outputFilePath}': {ex.Message}");
        }
    }

    private static byte[] DecodeToBgra(BcDecoder decoder, byte[] compressedData, int width, int height, CompressionFormat format)
    {
        if (compressedData == null || compressedData.Length == 0) return null;
        try
        {
            var decodedPixels = decoder.DecodeRaw(compressedData, width, height, format);
            if (decodedPixels == null || decodedPixels.Length == 0) return null;

            var bgraPixelData = new byte[decodedPixels.Length * 4];
            for (int i = 0; i < decodedPixels.Length; i++)
            {
                var pixel = decodedPixels[i];
                int offset = i * 4;
                bgraPixelData[offset + 0] = pixel.b;
                bgraPixelData[offset + 1] = pixel.g;
                bgraPixelData[offset + 2] = pixel.r;
                bgraPixelData[offset + 3] = pixel.a;
            }
            return bgraPixelData;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during texture decoding with format {format}: {ex.Message}");
            return null;
        }
    }

    private static void ReconstructNormalMapZChannel(byte[] bgraPixelData, int width, int height)
    {
        int pixelCount = width * height;
        for (int i = 0; i < pixelCount; i++)
        {
            int offset = i * 4;
            double normalX = (bgraPixelData[offset + 2] / 255.0) * 2.0 - 1.0; // Red
            double normalY = (bgraPixelData[offset + 1] / 255.0) * 2.0 - 1.0; // Green
            double zSquared = 1.0 - normalX * normalX - normalY * normalY;
            double normalZ = Math.Sqrt(Math.Max(0.0, zSquared));
            bgraPixelData[offset + 0] = (byte)(Math.Clamp(normalZ, 0.0, 1.0) * 255.0); // Blue
        }
    }
}


public class BlendShapeProcessor
{
    private static FMorphTargetDelta[] ParseDeltasFromBuffer(FMorphTargetVertexInfoBuffers buffer, int morphIndex)
    {
        var allDeltas = new List<FMorphTargetDelta>();

        // 1. このモーフターゲットが使用するバッチの範囲を特定する
        uint startBatchIndex = buffer.BatchStartOffsetPerMorph[morphIndex];
        uint numBatchesForMorph = buffer.BatchesPerMorph[morphIndex];
        uint endBatchIndex = startBatchIndex + numBatchesForMorph;

        // 2. 対応するバッチをすべてループする
        for (uint batchIdx = startBatchIndex; batchIdx < endBatchIndex; batchIdx++)
        {
            FMorphTargetVertexInfo batchInfo = buffer.MorphData[batchIdx];

            if (batchInfo.QuantizedDelta == null || batchInfo.QuantizedDelta.Length == 0) continue;

            // 3. バッチ内の各頂点データをループする
            foreach (FQuantizedDelta qDelta in batchInfo.QuantizedDelta)
            {
                // 4. 量子化された位置データをfloatのFVectorに復元（非量子化）する
                //    式: result = (quantized_value + min_value) * precision

                // PositionMinはint32なのでfloatにキャストが必要
                float deltaX = (qDelta.Position.X + batchInfo.PositionMin.X) * buffer.PositionPrecision;
                float deltaY = (qDelta.Position.Y + batchInfo.PositionMin.Y) * buffer.PositionPrecision;
                float deltaZ = (qDelta.Position.Z + batchInfo.PositionMin.Z) * buffer.PositionPrecision;

                var positionDelta = new FVector(deltaX, deltaY, deltaZ);

                FPackedNormal packedTangent;
                if (batchInfo.bTangents)
                {
                    // bTangentsがtrueの場合のみ計算を行う
                    // 計算式は位置の復元と全く同じ
                    float tangentDeltaX = (qDelta.TangentZ.X + batchInfo.TangentZMin.X) * buffer.TangentZPrecision;
                    float tangentDeltaY = (qDelta.TangentZ.Y + batchInfo.TangentZMin.Y) * buffer.TangentZPrecision;
                    float tangentDeltaZ = (qDelta.TangentZ.Z + batchInfo.TangentZMin.Z) * buffer.TangentZPrecision;

                    var tZDeltat = new Vector3(tangentDeltaX, tangentDeltaY, tangentDeltaZ);
                    var tNormalize = Vector3.Normalize(tZDeltat);  // Ensure unit length for normal;

                    var tangentZDelta = new FVector(tNormalize.X, tNormalize.Y, tNormalize.Z);

                    // FPackedNormalのバグのあるコンストラクタを避け、手動でパッキング処理を行う
                    uint packedX = (uint)Math.Clamp(Math.Round((tangentZDelta.X + 1.0f) * 127.5f), 0, 255);
                    uint packedY = (uint)Math.Clamp(Math.Round((tangentZDelta.Y + 1.0f) * 127.5f), 0, 255);
                    uint packedZ = (uint)Math.Clamp(Math.Round((tangentZDelta.Z + 1.0f) * 127.5f), 0, 255);

                    // 正しいビット演算で uint 型のデータを作成
                    uint packedData = packedX | (packedY << 8) | (packedZ << 16);

                    // uintを受け取るコンストラクタを使ってFPackedNormalインスタンスを生成
                    packedTangent = new FPackedNormal(packedData);
                }
                else
                {
                    // No tangents: default to zero or skip
                    packedTangent = new FPackedNormal(new FVector(0, 0, 0));
                }

                var delta = new FMorphTargetDelta(positionDelta, (FVector)packedTangent, qDelta.Index);
                allDeltas.Add(delta);
            }
        }


        return allDeltas.ToArray();
    }

    public static void ProcessBlendShapes(USkeletalMesh skeletalMesh, UsdGeomMesh usdMesh)
    {
        if (skeletalMesh.MorphTargets == null || skeletalMesh.MorphTargets.Length == 0)
        {
            return;
        }

        var lodModel = skeletalMesh.LODModels[0];
        var morphVertexInfo = lodModel.MorphTargetVertexInfoBuffers;

        // BlendShapes scopeを作成 (例: /Geo/BlendShapes)
        var stage = usdMesh.GetPrim().GetStage();
        var blendShapesScopePath = new SdfPath(USkeletalMeshToUSD.ScopePath + "/BlendShapes");
        var blendShapesScope = UsdGeomScope.Define(stage, blendShapesScopePath);

        // BlendShape名リスト
        var blendShapeNames = new VtTokenArray();
        int morphIndex = 0;
        foreach (var morphItem in skeletalMesh.MorphTargets)
        {
            if (morphItem.ResolvedObject == null) continue;
            if (!morphItem.ResolvedObject.TryLoad(out UObject obj) || obj is not UMorphTarget morphTarget) continue;

            var sanitizedName = USkeletalMeshToUSD.SanitizeUsdName(morphTarget.Name ?? "BlendShape");
            blendShapeNames.push_back(new TfToken(sanitizedName));

            // UsdSkelBlendShape prim定義
            var blendShapePath = blendShapesScope.GetPath().AppendChild(new TfToken(sanitizedName));
            var usdBlendShape = UsdSkelBlendShape.Define(stage, blendShapePath);

            // MorphTargetDeltasからデータ取得
            var deltas = morphTarget.MorphLODModels?[0].Vertices ?? Array.Empty<FMorphTargetDelta>();


            if (deltas.Length == 0 && lodModel.MorphTargetVertexInfoBuffers != null)
            {
                var buffer = lodModel.MorphTargetVertexInfoBuffers;

                // バッファからデルタを解析
                //deltas = ParseDeltasFromBuffer(buffer, morphItem.Index - 1);
                deltas = ParseDeltasFromBuffer(buffer, morphIndex);
            }
            morphIndex++;

            if (deltas.Length == 0) continue;

            // offsets (position deltas)
            var offsets = new VtVec3fArray((uint)deltas.Length);
            // normalOffsets (if available)
            var normalOffsets = new VtVec3fArray((uint)deltas.Length);
            // pointIndices (affected vertex indices)
            var pointIndices = new VtIntArray((uint)deltas.Length);

            for (int i = 0; i < deltas.Length; i++)
            {
                var delta = deltas[i];
                var ueDeltaPos = delta.PositionDelta * 0.01f; // Scale to meters
                var transformedDeltaPos = UsdCoordinateTransformer.TransformPosition(ueDeltaPos);
                //var transformedDeltaPos = ueDeltaPos;

                offsets[i] = new GfVec3f(transformedDeltaPos.X, transformedDeltaPos.Y, transformedDeltaPos.Z);

                // TangentZDelta as normal delta (assuming)
                var ueNormalDelta = delta.TangentZDelta;
                var transformedNormalDelta = UsdCoordinateTransformer.TransformNormal(ueNormalDelta);
                normalOffsets[i] = new GfVec3f(transformedNormalDelta.X, transformedNormalDelta.Y, transformedNormalDelta.Z);

                pointIndices[i] = (int)delta.SourceIdx;

            }

            usdBlendShape.CreateOffsetsAttr().Set(offsets);
            usdBlendShape.CreateNormalOffsetsAttr().Set(normalOffsets);
            usdBlendShape.CreatePointIndicesAttr().Set(pointIndices);
        }

        // MeshにblendShapesをbind
        if (blendShapeNames.size() > 0)
        {
            var skelBinding = UsdSkelBindingAPI.Apply(usdMesh.GetPrim());
            skelBinding.CreateBlendShapesAttr().Set(blendShapeNames);

            // Set relationships to blend shape prims
            var blendShapeTargetsRel = skelBinding.CreateBlendShapeTargetsRel();
            for (int i = 0; i < blendShapeNames.size(); i++)
            {
                var name = blendShapeNames[i];

                var targetPath = blendShapesScope.GetPath().AppendChild(name);
                blendShapeTargetsRel.AddTarget(targetPath);
            }

            // Set default weights (all 0)
            var weights = new VtFloatArray(blendShapeNames.size());
            for (int i = 0; i < blendShapeNames.size(); i++) weights[i] = 0.0f;
            usdMesh.GetPrim().CreateAttribute(new TfToken("primvars:skel:blendShapeWeights"), SdfValueTypeNames.FloatArray).Set(weights);
        }
    }

}