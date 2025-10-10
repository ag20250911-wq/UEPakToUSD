using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
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
using CUE4Parse.Utils;
using pxr;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Xml.Linq;

public static class USkeletalMeshToUSD
{
    private const string OriginalNameAttribute = "ue:originalName";
    private const string OriginalMaterialSlotNameAttribute = "ue:originalMaterialSlotName";
    private const string OriginalSkeletonNameAttribute = "ue:originalSkeletonName";
    private const string OriginalSkeletonPathAttribute = "ue:originalSkeletonPath";
    public static float UeToUsdScale = 0.01f; // UE units to meters

    public static string ScopePath { get; set; } = "/Geo";
    public static string ScopeMaterialsPath { get; set; } = "/Materials";
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


        Console.WriteLine($"Wrote Geo USD: {skeletalMesh.Name}");

        var originalAssetName = skeletalMesh.Name ?? "UnnamedSkeletalMesh";
        var sanitizedPrimName = SanitizeUsdName(originalAssetName);

        // ルートディレクトリを作成: outputDirectory/CharacterName/
        var rootDir = Path.Combine(outputDirectory, sanitizedPrimName);
        Directory.CreateDirectory(rootDir);

        // Geoディレクトリ: outputDirectory/CharacterName/Geo/
        var geoDir = Path.Combine(rootDir, "Geo");
        Directory.CreateDirectory(geoDir);

        // Materialsディレクトリ: outputDirectory/CharacterName/Geo/Materials/
        var materialsDir = Path.Combine(geoDir, "Materials");
        Directory.CreateDirectory(materialsDir);

        // Skelディレクトリ: outputDirectory/CharacterName/Skel/
        var skelDir = Path.Combine(rootDir, "Skel");
        Directory.CreateDirectory(skelDir);

        // Animsディレクトリ (オプション、将来のアニメーション用): outputDirectory/CharacterName/Skel/Anims/
        var animsDir = Path.Combine(skelDir, "Anims");
        Directory.CreateDirectory(animsDir);

        // ファイルパス
        var geoFile = Path.Combine(geoDir, sanitizedPrimName + "_geo.usda");
        var skeletonFile = Path.Combine(skelDir, sanitizedPrimName + "_skeleton.usda");
        //var rootFile = Path.Combine(rootDir, sanitizedPrimName + "_root.usda");
        var rootFile = Path.Combine(rootDir, sanitizedPrimName + ".usda");

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
        ProcessVerticesAndNormals(sourceVertices, out VtVec3fArray usdVertices, out VtVec3fArray usdNormals, out List<VtVec2fArray> usdUVs);

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
        WriteGeoUsd(geoFile, sanitizedPrimName, originalAssetName, usdVertices, usdNormals, usdUVs, sourceIndices, usdBoneWeights, usdBoneIndices, usdBones, elementSize, skeletalMesh, materialsDir);

        // 2) Skeleton ファイル作成（スケルトン / ジョイントXform）
        WriteSkeletonUsd(skeletonFile, sanitizedPrimName, skeletalMesh, sourceBoneInfo, usdBones, usedBoneIndices);

        // 3) Root ファイル作成（UsdSkelRoot を作り、geo/skeleton を reference で取り込む。さらに mesh -> skeleton の binding を作る）
        WriteRootUsd(rootFile, sanitizedPrimName, Path.GetRelativePath(rootDir, geoFile), Path.GetRelativePath(rootDir, skeletonFile));

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
    private static void WriteGeoUsd(string filePath, string primName, string originalAssetName, VtVec3fArray points, VtVec3fArray normals, List<VtVec2fArray> usdUVs, int[] indices, VtFloatArray jointWeights, VtIntArray jointIndices, VtTokenArray usdBones, uint elementSize, USkeletalMesh skeletalMesh, string materialsDir)
    {
        using (var stage = UsdStage.CreateNew(filePath))
        {
            if (stage == null) throw new InvalidOperationException($"Failed to create Geo USD: {filePath}");

            // 普通は /Geo をルートにする
            UsdGeom.UsdGeomSetStageUpAxis(stage, UsdGeomTokens.y);
            UsdGeom.UsdGeomSetStageMetersPerUnit(stage, 1);

            var geoScope = UsdGeomScope.Define(stage, new SdfPath(ScopePath)); // e.g. /Geo
            stage.SetDefaultPrim(geoScope.GetPrim());

            // マテリアル用のパス（ここでは /Materials としておくが、別ファイル化のためGeo内ではreferenceで取り込む準備）
            var materialsScope = UsdGeomScope.Define(stage, new SdfPath(ScopeMaterialsPath));

            var meshPath = geoScope.GetPath().AppendChild(new TfToken(primName));
            var usdMesh = UsdGeomMesh.Define(stage, meshPath);
            usdMesh.GetPrim().CreateAttribute(new TfToken(OriginalNameAttribute), SdfValueTypeNames.String).Set(originalAssetName);
            usdMesh.CreateOrientationAttr().Set(UsdGeomTokens.rightHanded);
            usdMesh.CreateSubdivisionSchemeAttr().Set(new TfToken("none"));

            // geometry attributes
            usdMesh.CreatePointsAttr().Set(points);
            usdMesh.CreateNormalsAttr().Set(normals);
            usdMesh.SetNormalsInterpolation(UsdGeomTokens.vertex);

            // UV0, UV1(LightMap), UV2... の設定
            var primvarsAPI = new UsdGeomPrimvarsAPI(usdMesh.GetPrim());
            for (int i = 0; i < usdUVs.Count; i++)
            {
                var st = "st";
                if (i != 0)
                    st += i.ToString();
                var uvPrimvar = primvarsAPI.CreatePrimvar(new TfToken(st), SdfValueTypeNames.TexCoord2fArray, UsdGeomTokens.vertex);
                uvPrimvar.GetAttr().Set(usdUVs[i]);
            }

            var faceVertexIndices = new VtIntArray((uint)indices.Length);
            for (int i = 0; i < indices.Length; i++) faceVertexIndices[i] = indices[i];
            usdMesh.CreateFaceVertexIndicesAttr().Set(faceVertexIndices);
            uint triCount = (uint)(indices.Length / 3);
            var faceVertexCounts = new VtIntArray(triCount);
            for (int i = 0; i < triCount; i++) faceVertexCounts[i] = 3;
            usdMesh.CreateFaceVertexCountsAttr().Set(faceVertexCounts);


            var lodModel = skeletalMesh.LODModels[0];

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

                    // マテリアル専用のディレクトリを作成: Geo/Materials/MatN/
                    var matDir = Path.Combine(materialsDir, sanitizedSubsetName);
                    Directory.CreateDirectory(matDir);

                    // マテリアル専用のUSDファイルパス: Geo/Materials/MatN/MatN.usd
                    var matFilePath = Path.Combine(matDir, sanitizedSubsetName + "_material.usda");

                    // マテリアルを別ファイルに書き出す
                    WriteMaterialUsd(matFilePath, sanitizedSubsetName, section, materialInterfaces, matDir, skeletalMesh, materialSlotName);

                    // Geo USD内でマテリアルのreferenceを追加
                    var matRefPath = new SdfPath(ScopeMaterialsPath).AppendChild(new TfToken(sanitizedSubsetName));
                    var usdMaterialPrim = stage.DefinePrim(matRefPath);
                    usdMaterialPrim.GetReferences().AddReference(new SdfReference(Path.GetRelativePath(Path.GetDirectoryName(filePath), matFilePath), new SdfPath("/" + sanitizedSubsetName)));

                    // サブセットにマテリアルをバインド
                    var subsetBindingAPI = UsdShadeMaterialBindingAPI.Apply(subset.GetPrim());
                    subsetBindingAPI.Bind(new UsdShadeMaterial(usdMaterialPrim));
                }
            }
            catch (Exception ex)
            {
                // サブセット処理中のエラーは無視し、処理を続行
                Console.WriteLine($"Warning: Failed during subset creation. {ex.Message}");
            }


            // スキニング属性（jointIndices / jointWeights）を頂点インターポレーションで付与
            // UsdSkelBinding は Root 側で行う予定だが、indices/weights自体はメッシュ側に含めておく
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


            stage.GetRootLayer().Save();
        }

        return;
    }

    private static void WriteMaterialUsd(string matFilePath, string sanitizedSubsetName, FSkelMeshSection section, ResolvedObject[] materialInterfaces, string matDir, USkeletalMesh skeletalMesh, string originalMaterialSlotName)
    {
        using (var matStage = UsdStage.CreateNew(matFilePath))
        {
            if (matStage == null) throw new InvalidOperationException($"Failed to create Material USD: {matFilePath}");

            UsdGeom.UsdGeomSetStageUpAxis(matStage, UsdGeomTokens.y);
            UsdGeom.UsdGeomSetStageMetersPerUnit(matStage, 1);

            // マテリアルPrimを定義
            var usdMaterial = UsdShadeMaterial.Define(matStage, new SdfPath("/" + sanitizedSubsetName));
            matStage.SetDefaultPrim(usdMaterial.GetPrim());

            usdMaterial.GetPrim().CreateAttribute(new TfToken(OriginalMaterialSlotNameAttribute), SdfValueTypeNames.String).Set(originalMaterialSlotName);

            var matPath = usdMaterial.GetPath().AppendChild(new TfToken("UsdPreviewSurface"));

            // PreviewSurface作成
            var pbrShader = UsdShadeShader.Define(matStage, new SdfPath(matPath + "/UsdPreviewSurface"));
            pbrShader.CreateIdAttr().Set(new TfToken("UsdPreviewSurface"));
            // シェーダーの surface 出力をマテリアルの surfaceOutput に接続
            usdMaterial.CreateSurfaceOutput()
                .ConnectToSource(pbrShader.ConnectableAPI(), new TfToken("surface"));

            // st Reader
            var stReader = UsdShadeShader.Define(matStage, matPath.AppendChild(new TfToken("texCoordReader")));
            stReader.CreateIdAttr(new TfToken("UsdPrimvarReader_float2"));
            stReader.CreateInput(new TfToken("varname"), SdfValueTypeNames.String).Set("st");

            // マテリアルが存在すればテクスチャをエクスポート
            if (section.MaterialIndex >= 0 && section.MaterialIndex < materialInterfaces.Length)
            {
                try
                {
                    var materialObject = materialInterfaces[section.MaterialIndex];
                    if (materialObject != null && materialObject.TryLoad(out UObject loadedMaterialObject))
                    {
                        Dictionary<string, string> exportedTextures = new Dictionary<string, string>();
                        bool isBaseMaterial = false;
                        if (loadedMaterialObject is UMaterialInstanceConstant materialInstanceConstant)
                        {
                            exportedTextures = UsdTextureExporter.ExportMaterialTextures(materialInstanceConstant, matDir);
                        }
                        else if (loadedMaterialObject is UMaterialInstance materialInstance)
                        {
                            Console.WriteLine($"Unsupported material type: {loadedMaterialObject.GetType().Name}");
                            exportedTextures = new Dictionary<string, string>();
                        }
                        else if (loadedMaterialObject is UMaterial material)
                        {
                            // UMaterial の処理: Expressionsからテクスチャを抽出
                            Console.WriteLine($"Processing base UMaterial: {material.Name}");
                            exportedTextures = UsdTextureExporter.ExportBaseMaterialTextures(material, matDir);
                            File.WriteAllText(Path.Combine(matDir, "isBaseMaterial"),"");
                            isBaseMaterial = true;
                        }
                        else
                        {
                            Console.WriteLine($"Unsupported material type: {loadedMaterialObject.GetType().Name}");
                            exportedTextures = new Dictionary<string, string>();
                        }

                        // exportedTexturesの値を相対パスに変換
                        var relativeTextures = exportedTextures.ToDictionary(
                            kv => kv.Key,
                            kv => "./Textures/" + Path.GetFileName(kv.Value)
                        );
                        // relativeTexturesをファイルに保存（例: JSON形式でデバッグ用）
                        var texturesFilePath = Path.Combine(matDir, "exportedTextures.json");
                        File.WriteAllText(texturesFilePath, Newtonsoft.Json.JsonConvert.SerializeObject(relativeTextures, Newtonsoft.Json.Formatting.Indented));
                        // テクスチャ接続をリファクタリングしたメソッドで処理
                        ConnectTexturesToShader(matStage, matPath, pbrShader, stReader, exportedTextures, matDir, isBaseMaterial);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Failed to process material at index {section.MaterialIndex}. {ex.Message}");
                }
            }

            matStage.GetRootLayer().Save();
        }
    }

    private static void ConnectTexturesToShader(UsdStage matStage, SdfPath matPath, UsdShadeShader pbrShader, UsdShadeShader stReader, Dictionary<string, string> exportedTextures, string matDir, bool isBaseMaterial)
    {
        // 接続されたテクスチャを追跡するための辞書（入力名 -> 相対パス）
        var connectedTextures = new Dictionary<string, string>();

        // パラメータ名で判別する場合のマッピング
        var textureMappings = new Dictionary<string, string[]>
    {
        { "diffuseColor", new[] { "BaseColor", "Albedo", "basecolor", "Base Color", "BaseColorTexture", "DiffuseMap", "Basecolor", "BaseColor/Opacity", "IrisColor" , "BaseColorSkeleton" } },
        { "normal", new[] { "BaseNormal", "Normal", "NormalMap", "NormalTexture", "ObjNormal" } },
        { "emissiveColor", new[] { "Emissive", "EmissiveColor", "EmissiveMap", "Emission" } },
        { "roughness", new[] { "Roughness", "RoughnessMap" } },
        { "metallic", new[] { "Metallic", "MetallicMap", "Metalness" } },
        { "specularColor", new[] { "Specular", "SpecularMap", "ScleraColor" } },
        { "clearcoat", new[] { "Clearcoat", "ClearcoatMap" } },
        { "clearcoatRoughness", new[] { "ClearcoatRoughness", "ClearcoatRoughnessMap" } },
        { "opacity", new[] { "Opacity", "OpacityMap", "Alpha" } },
        { "occlusion", new[] { "Occlusion", "OcclusionMap", "AmbientOcclusion" } },
        { "displacement", new[] { "Displacement", "DisplacementMap", "HeightMap" } }
    };

        // テクスチャ名で判別する場合のマッピング（Unreal Engineの一般的なサフィックスや命名規則に基づく）
        var textureNameMappings = new Dictionary<string, string[]>
    {
        { "diffuseColor", new[] { "_Albedo", "_BaseColor", "_Diffuse", "_Color" } },
        { "normal", new[] { "_Normal" } },
        { "emissiveColor", new[] { "_Emissive", "_Emission" } },
        { "roughness", new[] { "_Roughness" } },
        { "metallic", new[] { "_Metallic", "_Metalness" } },
        { "specularColor", new[] { "_Specular" } },
        { "clearcoat", new[] { "_Clearcoat" } },
        { "clearcoatRoughness", new[] { "_ClearcoatRoughness" } },
        { "opacity", new[] { "_Opacity", "_Alpha", "_Mask" } },
        { "occlusion", new[] { "_Occlusion", "_AmbientOcclusion" } },
        { "displacement", new[] { "_Disp", "_Displacement", "_Height" } }
    };

        // パックされたテクスチャのサポート（パラメータ名用）
        var packedTextureMappings = new Dictionary<string, (string[] keys, Dictionary<string, (string channel, string inputName)> channels)>
    {
        { "MRO", (new[] { "MRO", "MetallicRoughnessOcclusion", "PackedMRO" }, new Dictionary<string, (string, string)>
            {
                { "metallic", ("b", "metallic") },  // Blue channel for Metallic (GLTF standard)
                { "roughness", ("g", "roughness") }, // Green for Roughness
                { "occlusion", ("r", "occlusion") }  // Red for Occlusion
            })
        },
        { "MRSO", (new[] { "MRSOTexture"}, new Dictionary<string, (string, string)>
            {
                { "metallic", ("r", "metallic") },
                { "roughness", ("g", "roughness") }, // Roughness
                { "occlusion", ("a", "occlusion") }  // Occlusion
            })
        },
        { "ORM", (new[] { "ORMTexture" , "OcclusionRoughnessMetalic" }, new Dictionary<string, (string, string)>
            {
                { "metallic", ("b", "metallic") },  // Blue channel for Metallic (GLTF standard)
                { "roughness", ("g", "roughness") }, // Green for Roughness
                { "occlusion", ("r", "occlusion") }  // Red for Occlusion
            })
        },
        { "SASR", (new[] { "SASR", "SubsurfaceAmbientSpecularRoughness", "PackedSASR", "SASRTexture" }, new Dictionary<string, (string, string)>
            {
                { "occlusion", ("g", "occlusion") },
                { "specular", ("b", "specularLevel") }, // specularLevelにマップ（useSpecularWorkflow=1の場合）
                { "roughness", ("a", "roughness") }
            })
        },
        { "FAMR", (new[] { "FAMR", "FuzzAmbientMetallicRoughness", "PackedFAMR" , "COMRTexture" , "Metallic/Roughness/Specular" }, new Dictionary<string, (string, string)>
            {
                { "occlusion", ("g", "occlusion") },    // G: Ambient Occlusion
                { "metallic", ("b", "metallic") },      // B: Metallic
                { "roughness", ("a", "roughness") }     // A: Roughness
            })
        }
    };

        // パックされたテクスチャのサポート（テクスチャ名用、サフィックスベース）
        var packedTextureNameMappings = new Dictionary<string, (string[] keys, Dictionary<string, (string channel, string inputName)> channels)>
    {
        { "MRO", (new[] { "_MRO", "_MetallicRoughnessOcclusion" }, new Dictionary<string, (string, string)>
            {
                { "metallic", ("b", "metallic") },
                { "roughness", ("g", "roughness") },
                { "occlusion", ("r", "occlusion") }
            })
        },
        { "MRSO", (new[] { "_MRSO"}, new Dictionary<string, (string, string)>
            {
                { "metallic", ("r", "metallic") },
                { "roughness", ("g", "roughness") },
                { "occlusion", ("a", "occlusion") }
            })
        },
        { "ORM", (new[] { "_ORM" , "_OcclusionRoughnessMetallic" }, new Dictionary<string, (string, string)>
            {
                { "metallic", ("b", "metallic") },
                { "roughness", ("g", "roughness") },
                { "occlusion", ("r", "occlusion") }
            })
        },
        { "SASR", (new[] { "_SASR", "_SubsurfaceAmbientSpecularRoughness" }, new Dictionary<string, (string, string)>
            {
                { "occlusion", ("g", "occlusion") },
                { "specular", ("b", "specularLevel") },
                { "roughness", ("a", "roughness") }
            })
        },
        { "FAMR", (new[] { "_FAMR", "_FuzzAmbientMetallicRoughness", "_COMR" , "_MetallicRoughnessSpecular" }, new Dictionary<string, (string, string)>
            {
                { "occlusion", ("g", "occlusion") },
                { "metallic", ("b", "metallic") },
                { "roughness", ("a", "roughness") }
            })
        }
    };

        // 使用するマッピングを選択
        var currentTextureMappings = isBaseMaterial ? textureNameMappings : textureMappings;
        var currentPackedMappings = isBaseMaterial ? packedTextureNameMappings : packedTextureMappings;

        // 個別テクスチャの接続
        foreach (var mapping in currentTextureMappings)
        {
            string texturePath = null;
            foreach (var kv in exportedTextures)
            {
                string checkString = isBaseMaterial ? Path.GetFileNameWithoutExtension(kv.Value) : kv.Key;
                if (mapping.Value.Any(m => checkString.Contains(m, StringComparison.OrdinalIgnoreCase)))
                {
                    texturePath = kv.Value;
                    break;
                }
            }

            if (!string.IsNullOrEmpty(texturePath))
            {
                var relativePath = "./Textures/" + Path.GetFileName(texturePath);
                ConnectSingleTexture(matStage, matPath, pbrShader, stReader, mapping.Key, texturePath, "rgb", mapping.Key);
                connectedTextures[mapping.Key] = relativePath;
            }
        }

        // パックされたテクスチャの接続
        foreach (var packed in currentPackedMappings)
        {
            string packedTexturePath = null;
            foreach (var kv in exportedTextures)
            {
                string checkString = isBaseMaterial ? Path.GetFileNameWithoutExtension(kv.Value) : kv.Key;
                if (packed.Value.keys.Any(k => checkString.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    packedTexturePath = kv.Value;
                    break;
                }
            }

            if (!string.IsNullOrEmpty(packedTexturePath))
            {
                var relativePath = "./Textures/" + Path.GetFileName(packedTexturePath);

                var shaderName = packed.Key + "Texture";
                var texShader = UsdShadeShader.Define(matStage, new SdfPath(matPath + "/" + shaderName));
                texShader.CreateIdAttr().Set(new TfToken("UsdUVTexture"));
                texShader.CreateInput(new TfToken("file"), SdfValueTypeNames.Asset).Set(new SdfAssetPath("./Textures/" + Path.GetFileName(packedTexturePath)));
                texShader.CreateInput(new TfToken("st"), SdfValueTypeNames.Float2).ConnectToSource(stReader.ConnectableAPI(), new TfToken("result"));

                // 各チャンネルを出力として作成
                texShader.CreateOutput(new TfToken("r"), SdfValueTypeNames.Float);
                texShader.CreateOutput(new TfToken("g"), SdfValueTypeNames.Float);
                texShader.CreateOutput(new TfToken("b"), SdfValueTypeNames.Float);
                texShader.CreateOutput(new TfToken("a"), SdfValueTypeNames.Float);

                // 各チャンネルを対応する入力に接続
                foreach (var channelMap in packed.Value.channels)
                {
                    var outputName = channelMap.Value.channel;
                    var inputName = channelMap.Value.inputName;
                    pbrShader.CreateInput(new TfToken(inputName), GetSdfTypeForInput(inputName))
                        .ConnectToSource(texShader.ConnectableAPI(), new TfToken(outputName));
                    connectedTextures[inputName] = relativePath + " (channel: " + outputName + ")";
                }
            }
        }

        // specularWorkflowの設定（metallicがある場合0、specularがある場合1）
        bool hasMetallic = connectedTextures.ContainsKey("metallic");
        bool hasSpecular = connectedTextures.ContainsKey("specularColor") || connectedTextures.ContainsKey("specularLevel");
        int workflow = hasSpecular && !hasMetallic ? 1 : 0;
        pbrShader.CreateInput(new TfToken("useSpecularWorkflow"), SdfValueTypeNames.Int).Set(workflow);

        // 接続されたテクスチャをJSONファイルに出力
        var connectedTexturesFilePath = Path.Combine(matDir, "connectedTextures.json");
        File.WriteAllText(connectedTexturesFilePath, Newtonsoft.Json.JsonConvert.SerializeObject(connectedTextures, Newtonsoft.Json.Formatting.Indented));
    }

    private static void ConnectSingleTexture(UsdStage matStage, SdfPath matPath, UsdShadeShader pbrShader, UsdShadeShader stReader, string shaderSuffix, string texturePath, string outputName, string inputName)
    {
        var texShader = UsdShadeShader.Define(matStage, new SdfPath(matPath + "/" + shaderSuffix + "Texture"));
        texShader.CreateIdAttr().Set(new TfToken("UsdUVTexture"));
        texShader.CreateInput(new TfToken("file"), SdfValueTypeNames.Asset).Set(new SdfAssetPath("./Textures/" + Path.GetFileName(texturePath)));
        texShader.CreateInput(new TfToken("st"), SdfValueTypeNames.Float2).ConnectToSource(stReader.ConnectableAPI(), new TfToken("result"));

        texShader.CreateOutput(new TfToken(outputName), GetSdfTypeForOutput(outputName));

        pbrShader.CreateInput(new TfToken(inputName), GetSdfTypeForInput(inputName))
            .ConnectToSource(texShader.ConnectableAPI(), new TfToken(outputName));
    }

    private static SdfValueTypeName GetSdfTypeForInput(string inputName)
    {
        return inputName switch
        {
            "diffuseColor" or "emissiveColor" or "specularColor" => SdfValueTypeNames.Color3f,
            "normal" => SdfValueTypeNames.Normal3f,
            "displacement" => SdfValueTypeNames.Float3,
            _ => SdfValueTypeNames.Float // metallic, roughness, etc.
        };
    }

    private static SdfValueTypeName GetSdfTypeForOutput(string outputName)
    {
        return outputName switch
        {
            "rgb" => SdfValueTypeNames.Float3,
            _ => SdfValueTypeNames.Float // r, g, b, a
        };
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
            // Skeleton Primに元のスケルトン名とパスをue:originalSkeletonName, ue:originalSkeletonPathとして追加
            var skeletonPrim = usdSkeleton.GetPrim();
            if (skeletalMesh.Skeleton.ResolvedObject != null)
            {
                skeletonPrim.CreateAttribute(new TfToken(OriginalSkeletonNameAttribute), SdfValueTypeNames.String).Set(skeletalMesh.Skeleton.ResolvedObject.Name.Text);
                skeletonPrim.CreateAttribute(new TfToken(OriginalSkeletonPathAttribute), SdfValueTypeNames.String).Set(skeletalMesh.Skeleton.ResolvedObject.GetPathName());
            }


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
                var uePos = item.Translation * UeToUsdScale;
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

                // ボーンのPrimに元のボーン名をue:originalNameアトリビュートとして追加
                var originalBoneName = skeletalMesh.ReferenceSkeleton.FinalRefBoneInfo[originalIndex].Name.Text;
                usdXform.GetPrim().CreateAttribute(new TfToken(OriginalNameAttribute), SdfValueTypeNames.String).Set(originalBoneName);

                usdXforms.Add(usdXform);
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

        return;
    }

    // Root USD を作り、geo/skeleton を reference で取り込む（結果として一つの SkelRoot 配下に統合される）
    private static void WriteRootUsd(string filePath, string primName, string geoFileRelPath, string skeletonFileRelPath)
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
            geoPrim.GetReferences().AddReference(new SdfReference(geoFileRelPath, new SdfPath(ScopePath)));

            // 同様に /SkelRoot/Skeleton を作り、skeletonFile を reference
            var skeletonTargetPath = skelRootPath.AppendPath(new SdfPath(ScopeSkeletonPath.TrimStart('/'))); // /SkelRoot/Skeleton
            var skeletonPrim = stage.DefinePrim(skeletonTargetPath);
            skeletonPrim.GetReferences().AddReference(new SdfReference(skeletonFileRelPath, new SdfPath(ScopeSkeletonPath)));

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

            // /SkelRoot/Materials を作り、それに geoFile のマテリアル /Materials を reference で /SkelRoot/Materials にマッピング
            var matsTargetPath = skelRootPath.AppendPath(new SdfPath(ScopeMaterialsPath.TrimStart('/'))); // /SkelRoot/Materials
            //var matsTargetPath = new SdfPath(ScopeMaterialsPath); // /Materials
            var matsPrim = stage.DefinePrim(matsTargetPath);
            matsPrim.GetReferences().AddReference(new SdfReference(geoFileRelPath, new SdfPath(ScopeMaterialsPath)));

            // /SkelRoot/SkeletonsXform を作り、それに skeletonFilePath のXform /Materials を reference で /SkelRoot/SkeletonsXform にマッピング
            var xformTargetPath = skelRootPath.AppendPath(new SdfPath(ScopeSkeletonsXformPath.TrimStart('/'))); // /SkelRoot/SkeletonsXform 
            var xformPrim = stage.DefinePrim(xformTargetPath);
            xformPrim.GetReferences().AddReference(new SdfReference(skeletonFileRelPath, new SdfPath(ScopeSkeletonsXformPath)));

            // 全てのsubsetにマテリアルを再割り当て
            var subsets = UsdGeomSubset.GetGeomSubsets(UsdGeomImageable.Get(stage, meshPrimPath));
            foreach (var subset in subsets)
            {
                var bindingApiSubset = UsdShadeMaterialBindingAPI.Apply(subset.GetPrim());
                //var materialPath = new SdfPath(ScopeMaterialsPath).AppendChild(new TfToken(subset.GetPrim().GetName()));// /Materials/MTL_Ene_Def_ChestPouchWaistBelt
                var materialPath = matsTargetPath.AppendChild(new TfToken(subset.GetPrim().GetName()));
                var usdMaterial = UsdShadeMaterial.Get(stage, materialPath);
                if (usdMaterial)
                {
                    bindingApiSubset.Bind(usdMaterial);
                }
            }


            stage.GetRootLayer().Save();
        }

        // 重要: root.usda に記述した reference は相対パスとして機能する

        return;
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

    private static void ProcessVerticesAndNormals(FGPUVertFloat[] sourceVertices, out VtVec3fArray usdVertices, out VtVec3fArray usdNormals, out List<VtVec2fArray> usdUVs)
    {
        usdVertices = new VtVec3fArray((uint)sourceVertices.Length);
        usdNormals = new VtVec3fArray((uint)sourceVertices.Length);

        // 可変長UVsに対応
        usdUVs = new List<VtVec2fArray>();
        var uvcount = sourceVertices.Max(x => x.UV.Length);
        for (int i = 0; i < uvcount; i++)
        {
            usdUVs.Add(new VtVec2fArray((uint)sourceVertices.Length));
        }

        for (int i = 0; i < sourceVertices.Length; i++)
        {
            var vertex = sourceVertices[i];
            var uePos = vertex.Pos * UeToUsdScale; // Scale to meters
            var ueNormal = vertex.Normal[2]; // 3つ目が法線

            for (int x = 0; x < uvcount; x++)
            {
                usdUVs[x][i] = new GfVec2f(vertex.UV[x].U, 1.0f - vertex.UV[x].V);// UEのV座標は反転が必要な場合が多い
            }

            var transformedPos = UsdCoordinateTransformer.TransformPosition(uePos);
            var transformedNormal = UsdCoordinateTransformer.TransformNormal(ueNormal);


            usdVertices[i] = new GfVec3f(transformedPos.X, transformedPos.Y, transformedPos.Z);
            usdNormals[i] = new GfVec3f(transformedNormal.X, transformedNormal.Y, transformedNormal.Z);
        }

        return;
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

        return;
    }

    public static VtTokenArray BuildUsdBonePaths(FMeshBoneInfo[] boneInfo, List<int> usedBoneIndices)
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

            // UsdSkelBlendShape Prim定義
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
                var ueDeltaPos = delta.PositionDelta * USkeletalMeshToUSD.UeToUsdScale; // Scale to meters
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

        return;
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
    public static Dictionary<string, string> ExportMaterialTextures(UMaterialInstanceConstant materialInstance, string outputDirectory)
    {
        var result = new Dictionary<string, string>();
        if (materialInstance == null) return result;

        var materialDirectoryPath = Path.Combine(outputDirectory, "Textures");
        Directory.CreateDirectory(materialDirectoryPath);
        string outputFilePath;
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

                Console.WriteLine($"Export: {texture2D.Name}");

                // StreamingVirtualTexture
                if (texture2D.PlatformData?.VTData != null)
                {
                    outputFilePath = Path.Combine(materialDirectoryPath, texture2D.Name) + (texture2D.IsHDR ? ".tiff" : ".png");
                    VirtualTextureExporter.ExportVirtualTexture(decoder, texture2D, outputFilePath);
                    // 保存したテクスチャのパスを辞書に登録
                    result[textureParameter.ParameterInfo.Name.PlainText] = outputFilePath;
                    continue;
                }

                var mipMap = texture2D.GetFirstMip();
                if (mipMap?.BulkData?.Data == null || mipMap.SizeX == 0 || mipMap.SizeY == 0) continue;

                var compressedData = mipMap.BulkData.Data;
                int width = mipMap.SizeX;
                int height = mipMap.SizeY;

                byte[] decodedPixelData = DecodeTextureData(decoder, texture2D, compressedData, width, height);

                if (decodedPixelData == null) continue;

                if (texture2D.IsTextureCube) continue;

                outputFilePath = Path.Combine(materialDirectoryPath, texture2D.Name) + (texture2D.IsHDR ? ".tiff" : ".png");
                SaveTextureImage(decodedPixelData, width, height, texture2D.IsHDR, outputFilePath, texture2D.Format);
                // 保存したテクスチャのパスを辞書に登録
                result[textureParameter.ParameterInfo.Name.PlainText] = outputFilePath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to export a texture parameter. {ex.Message}");
            }
        }

        return result;
    }

    public static Dictionary<string, string> ExportBaseMaterialTextures(UMaterial material, string outputDirectory)
    {
        var result = new Dictionary<string, string>();
        if (material == null) return result;

        var materialDirectoryPath = Path.Combine(outputDirectory, "Textures");
        Directory.CreateDirectory(materialDirectoryPath);
        string outputFilePath;
        var decoder = new BcDecoder();

        List<UTexture> referencedTextures = material.ReferencedTextures ?? new List<UTexture>();

        foreach (var tex in referencedTextures)
        {
            try
            {
                if (tex == null) continue;
                if (tex is not UTexture2D texture2D)
                {
                    continue;
                }

                Console.WriteLine($"Export from base material: {texture2D.Name}");


                string key = texture2D.Name;
                // StreamingVirtualTexture
                if (texture2D.PlatformData?.VTData != null)
                {
                    outputFilePath = Path.Combine(materialDirectoryPath, texture2D.Name) + (texture2D.IsHDR ? ".tiff" : ".png");
                    VirtualTextureExporter.ExportVirtualTexture(decoder, texture2D, outputFilePath);
                    // 保存したテクスチャのパスを辞書に登録
                    result[key] = outputFilePath;
                    continue;
                }



                var mipMap = texture2D.GetFirstMip();
                if (mipMap?.BulkData?.Data == null || mipMap.SizeX == 0 || mipMap.SizeY == 0) continue;

                var compressedData = mipMap.BulkData.Data;
                int width = mipMap.SizeX;
                int height = mipMap.SizeY;

                byte[] decodedPixelData = DecodeTextureData(decoder, texture2D, compressedData, width, height);

                if (decodedPixelData == null) continue;

                if (texture2D.IsTextureCube) continue;

                outputFilePath = Path.Combine(materialDirectoryPath, texture2D.Name) + (texture2D.IsHDR ? ".tiff" : ".png");
                SaveTextureImage(decodedPixelData, width, height, texture2D.IsHDR, outputFilePath, texture2D.Format);
                result[key] = outputFilePath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to export a texture from base material. {ex.Message}");
            }
        }

        return result;
    }

    public static byte[] DecodeTextureData(BcDecoder decoder, UTexture2D texture2D, byte[] rawData, int width, int height)
    {
        byte[] decodedPixelData = null;

        switch (texture2D.Format)
        {
            case EPixelFormat.PF_B8G8R8A8:
                // Already in BGRA format, 4 bytes per pixel
                decodedPixelData = rawData;
                break;
            case EPixelFormat.PF_G8:
                // Grayscale, 1 byte per pixel - convert to BGRA
                decodedPixelData = new byte[width * height * 4];
                for (int i = 0; i < width * height; i++)
                {
                    byte gray = rawData[i];
                    int offset = i * 4;
                    decodedPixelData[offset + 0] = gray; // B
                    decodedPixelData[offset + 1] = gray; // G
                    decodedPixelData[offset + 2] = gray; // R
                    decodedPixelData[offset + 3] = 255;  // A
                }
                break;
            case EPixelFormat.PF_FloatRGBA when texture2D.IsHDR:
                // FloatRGBA, 16 bytes per pixel (4 floats) - convert to RgbaVector (but since it's byte[], it's already in little-endian float bytes)
                // For saving, we can pass it directly if using RgbaVector which expects float components
                decodedPixelData = rawData;
                break;
            case EPixelFormat.PF_DXT1:
                decodedPixelData = DecodeToBgra(decoder, rawData, width, height, CompressionFormat.Bc1);
                break;
            case EPixelFormat.PF_DXT5:
                decodedPixelData = DecodeToBgra(decoder, rawData, width, height, CompressionFormat.Bc3);
                break;
            case EPixelFormat.PF_BC5:
                decodedPixelData = DecodeToBgra(decoder, rawData, width, height, CompressionFormat.Bc5);
                if (texture2D.IsNormalMap && decodedPixelData != null)
                {
                    ReconstructNormalMapZChannel(decodedPixelData, width, height);
                }
                break;
            case EPixelFormat.PF_BC7:
                decodedPixelData = DecodeToBgra(decoder, rawData, width, height, CompressionFormat.Bc7);
                break;
            default:
                return null;
        }

        return decodedPixelData;
    }

    public static void SaveTextureImage(byte[] decodedPixelData, int width, int height, bool isHDR, string outputFilePath, EPixelFormat format)
    {
        try
        {
            if (isHDR)
            {
                // For HDR, assume data is in float format (16 bytes per pixel for FloatRGBA)
                if (format == EPixelFormat.PF_FloatRGBA)
                {
                    // Directly load from byte[] as ReadOnlySpan<byte>, internally cast to RgbaVector
                    using (var image = Image.LoadPixelData<RgbaVector>(decodedPixelData, width, height))
                    {
                        var encoder = new TiffEncoder() { BitsPerPixel = TiffBitsPerPixel.Bit64 };
                        image.SaveAsTiff(outputFilePath, encoder);
                    }
                }
                else
                {
                    // Fallback or error
                    Console.WriteLine($"Unsupported HDR format for saving: {format}");
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

        return;
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

        return null;
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

        return;
    }
}
public static class VirtualTextureExporter
{
    public static void ExportVirtualTexture(BcDecoder decoder, UTexture2D texture2D, string outputFilePath)
    {
        if (texture2D.PlatformData?.VTData == null) return;

        var vtData = texture2D.PlatformData.VTData;

        // 最高品質のトップミップ（インデックス0）を使用
        int mipIndex = 0;
        uint chunkIndex = vtData.ChunkIndexPerMip[mipIndex];
        var mipData = vtData.TileOffsetData[mipIndex];

        // 基本寸法を抽出
        int totalWidth = (int)vtData.Width;
        int totalHeight = (int)vtData.Height;
        int tileSize = (int)vtData.TileSize;
        int borderSize = (int)vtData.TileBorderSize;
        int numTilesX = (int)mipData.Width;
        int numTilesY = (int)mipData.Height;

        // 圧縮フォーマットに基づくブロックバイト数を取得（非圧縮時は0）
        int bytesPerBlock = GetBytesPerBlock(texture2D.Format);
        if (bytesPerBlock < 0) // 未対応のフォーマット
        {
            Console.WriteLine($"VTData未対応の圧縮フォーマット: {texture2D.Format}");
            return;
        }

        bool isCompressed = bytesPerBlock > 0;
        int bytesPerPixel = isCompressed ? 4 : GetBytesPerPixel(texture2D.Format); // 非圧縮時はフォーマットに応じたバイト数
        if (!isCompressed && bytesPerPixel == 0)
        {
            Console.WriteLine($"VTData未対応の非圧縮フォーマット: {texture2D.Format}");
            return;
        }

        int paddedTileSize = tileSize + borderSize * 2;
        int bytesPerTile;
        if (isCompressed)
        {
            int blockSize = 4; // BCフォーマットは4x4ブロック
            int blocksPerSide = paddedTileSize / blockSize;
            bytesPerTile = blocksPerSide * blocksPerSide * bytesPerBlock;
            //Console.WriteLine($"タイルあたりのバイト数 (圧縮): {bytesPerTile}");
        }
        else
        {
            bytesPerTile = paddedTileSize * paddedTileSize * bytesPerPixel;
            //Console.WriteLine($"タイルあたりのバイト数 (非圧縮): {bytesPerTile}");
        }

        // 最終画像バッファを準備 (出力は常にRGBA形式: HDRならFloatRGBA (16バイト)、それ以外はB8G8R8A8 (4バイト))
        int finalBytesPerPixel = texture2D.IsHDR ? 16 : 4;
        var finalImageData = new byte[totalWidth * totalHeight * finalBytesPerPixel];

        // ミップのチャンクとベースオフセットを取得
        var chunk = vtData.Chunks[chunkIndex];
        long baseOffsetMip = vtData.BaseOffsetPerMip[chunkIndex];

        // すべてのタイルを処理
        for (int tileY = 0; tileY < numTilesY; tileY++)
        {
            for (int tileX = 0; tileX < numTilesX; tileX++)
            {
                // タイルのモートンエンコードされた仮想アドレスを計算
                uint vAddress = MathUtils.MortonCode2((uint)tileX) | (MathUtils.MortonCode2((uint)tileY) << 1);

                // ミップデータ内の相対タイルオフセットを取得
                uint tileOffsetInMip = mipData.GetTileOffset(vAddress);
                if (tileOffsetInMip == ~0u)
                {
                    // このタイルにデータなし; スキップ（必要に応じてデフォルト色で塗りつぶし可能）
                    continue;
                }

                // チャンクデータ内の最終オフセットを計算
                long finalOffset = baseOffsetMip + (long)tileOffsetInMip * bytesPerTile;
                if (finalOffset + bytesPerTile > chunk.BulkData.Data.Length)
                {
                    Console.WriteLine($"エラー: タイル ({tileX}, {tileY}) のオフセットが範囲外");
                    continue;
                }

                // タイルデータを抽出
                var tileData = new byte[bytesPerTile];
                Array.Copy(chunk.BulkData.Data, finalOffset, tileData, 0, bytesPerTile);

                // パッド付きタイルをデコードまたは準備
                byte[] decompressedPaddedTile;
                if (isCompressed)
                {
                    decompressedPaddedTile = UsdTextureExporter.DecodeTextureData(decoder, texture2D, tileData, paddedTileSize, paddedTileSize);
                    if (decompressedPaddedTile == null) continue;
                }
                else
                {
                    decompressedPaddedTile = ConvertToRgbaIfNeeded(tileData, paddedTileSize * paddedTileSize, texture2D.Format, bytesPerPixel, finalBytesPerPixel);
                }

                // 中央部（境界なし）を最終画像にコピー
                CopyTileToImage(decompressedPaddedTile, finalImageData, tileX, tileY, tileSize, borderSize, paddedTileSize, totalWidth, finalBytesPerPixel);
            }
        }

        // アセンブルされた画像を保存
        UsdTextureExporter.SaveTextureImage(finalImageData, totalWidth, totalHeight, texture2D.IsHDR, outputFilePath, texture2D.Format);

        return;
    }

    private static int GetBytesPerBlock(EPixelFormat format)
    {
        return format switch
        {
            EPixelFormat.PF_DXT1 => 8,     // BC1
            EPixelFormat.PF_DXT5 => 16,    // BC3
            EPixelFormat.PF_BC5 => 16,
            EPixelFormat.PF_BC7 => 16,
            EPixelFormat.PF_BC6H => 16,
            EPixelFormat.PF_B8G8R8A8 => 0,  // 非圧縮
            EPixelFormat.PF_FloatRGBA => 0, // 非圧縮
            EPixelFormat.PF_G8 => 0,        // 非圧縮
            _ => -1  // 未対応
        };
    }

    private static int GetBytesPerPixel(EPixelFormat format)
    {
        return format switch
        {
            EPixelFormat.PF_B8G8R8A8 => 4,
            EPixelFormat.PF_FloatRGBA => 16,
            EPixelFormat.PF_G8 => 1,
            _ => 0  // 未対応
        };
    }

    private static byte[] ConvertToRgbaIfNeeded(byte[] sourceData, int pixelCount, EPixelFormat format, int sourceBytesPerPixel, int targetBytesPerPixel)
    {
        if (sourceBytesPerPixel == targetBytesPerPixel)
        {
            return sourceData; // 変換不要 (例: PF_B8G8R8A8 -> 4バイトRGBA, PF_FloatRGBA -> 16バイトFloatRGBA)
        }

        // 変換が必要な場合 (例: PF_G8 -> 4バイトRGBA)
        var convertedData = new byte[pixelCount * targetBytesPerPixel];

        switch (format)
        {
            case EPixelFormat.PF_G8:
                for (int i = 0; i < pixelCount; i++)
                {
                    byte gray = sourceData[i];
                    int offset = i * 4;
                    convertedData[offset] = gray;     // B
                    convertedData[offset + 1] = gray; // G
                    convertedData[offset + 2] = gray; // R
                    convertedData[offset + 3] = 255;  // A
                }
                break;
            // 他のフォーマットが必要に応じて追加
            default:
                Console.WriteLine($"変換未対応のフォーマット: {format}");
                return null;
        }

        return convertedData;
    }

    private static void CopyTileToImage(byte[] sourceTile, byte[] destImage, int tileX, int tileY, int tileSize, int borderSize, int paddedTileSize, int totalWidth, int bytesPerPixel)
    {
        int destBaseX = tileX * tileSize;
        int destBaseY = tileY * tileSize;
        int copyRowBytes = tileSize * bytesPerPixel;

        for (int y = 0; y < tileSize; y++)
        {
            int srcY = y + borderSize;
            int destY = destBaseY + y;

            int srcOffset = (srcY * paddedTileSize + borderSize) * bytesPerPixel;
            int destOffset = (destY * totalWidth + destBaseX) * bytesPerPixel;

            Array.Copy(sourceTile, srcOffset, destImage, destOffset, copyRowBytes);
        }

        return;
    }
}