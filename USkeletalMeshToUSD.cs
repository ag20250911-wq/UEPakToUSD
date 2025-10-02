using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using pxr;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

public static class USkeletalMeshToUSD
{
    // 元の Unreal Engine アセット名を格納するためのカスタム属性名
    private const string OriginalNameAttribute = "UEOriginalName";
    // 元の Unreal Engine マテリアルスロット名を格納するためのカスタム属性名
    private const string OriginalMaterialSlotNameAttribute = "UEOriginalMaterialSlotName";

    // デフォルトの USD Scope パス（実行時に変更可能）
    // 例: USkeletalMeshToUSD.ScopePath = "/MyCustomGeo";
    public static string ScopePath { get; set; } = "/Geo";

    /// <summary>
    /// USDの命名規則に準拠するように文字列をサニタイズします。
    /// 英数字とアンダースコアのみを許可し、先頭が数字の場合はアンダースコアを付与します。
    /// </summary>
    private static string SanitizeUsdName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "Unnamed";
        }

        var sanitizedBuilder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')
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
    /// USkeletalMeshをUSDファイルに変換し、指定されたディレクトリに保存します。
    /// </summary>
    /// <param name="skeletalMesh">変換対象のスケルタルメッシュ</param>
    /// <param name="outputDirectory">出力先ディレクトリ</param>
    public static void Convert(USkeletalMesh skeletalMesh, string outputDirectory)
    {
        if (skeletalMesh == null) throw new ArgumentNullException(nameof(skeletalMesh));
        if (string.IsNullOrEmpty(outputDirectory)) throw new ArgumentNullException(nameof(outputDirectory));

        Directory.CreateDirectory(outputDirectory);

        var originalAssetName = skeletalMesh.Name ?? "UnnamedSkeletalMesh";
        var sanitizedPrimName = SanitizeUsdName(originalAssetName);
        var outputFilePath = Path.Combine(outputDirectory, sanitizedPrimName + ".usda");

        using (var stage = UsdStage.CreateNew(outputFilePath))
        {
            if (stage == null)
            {
                throw new InvalidOperationException($"Failed to create USD stage at '{outputFilePath}'");
            }
            UsdGeom.UsdGeomSetStageUpAxis(stage, UsdGeomTokens.y); // Y-Up
            UsdGeom.UsdGeomSetStageMetersPerUnit(stage, 1);


            // --- ルートプリムとスコープの作成 --- /Geo/PrimName
            var scope = UsdGeomScope.Define(stage, ScopePath);
            stage.SetDefaultPrim(scope.GetPrim());
            var usdMesh = UsdGeomMesh.Define(stage, scope.GetPath().AppendChild(new TfToken(sanitizedPrimName)));
            usdMesh.GetPrim().CreateAttribute(new TfToken(OriginalNameAttribute), Sdf.SdfGetValueTypeString()).Set(originalAssetName);
            // --- メッシュの向きが右手系であることを指定 --- 
            usdMesh.CreateOrientationAttr().Set(UsdGeomTokens.rightHanded);
            // --- スムーズシェーディングを無効にする ---
            usdMesh.CreateSubdivisionSchemeAttr(new TfToken("none"));

            // --- ジオメトリデータ（頂点、インデックス）の処理 ---
            if (skeletalMesh.LODModels == null || skeletalMesh.LODModels.Length == 0)
            {
                return; // エクスポートするデータがない
            }

            var lodModel = skeletalMesh.LODModels[0];
            if (lodModel.VertexBufferGPUSkin?.VertsFloat == null || lodModel.Indices == null)
            {
                return;
            }

            var sourceVertices = lodModel.VertexBufferGPUSkin.VertsFloat;
            int[] sourceIndices;
            if (lodModel.Indices.Indices16 != null && lodModel.Indices.Indices16.Length > 0)
            {
                sourceIndices = Array.ConvertAll(lodModel.Indices.Indices16, i => (int)i);
            }
            else if (lodModel.Indices.Indices32 != null && lodModel.Indices.Indices32.Length > 0)
            {
                sourceIndices = lodModel.Indices.Indices32.Select(i => (int)i).ToArray();
            }
            else
            {
                sourceIndices = Array.Empty<int>();
            }

            if (sourceVertices.Length == 0 || sourceIndices.Length == 0)
            {
                return; // エクスポートするデータがない
            }

            var sourceBoneInfo = skeletalMesh.ReferenceSkeleton.FinalRefBoneInfo;
            var sourceBoneIndexMap = skeletalMesh.ReferenceSkeleton.FinalNameToIndexMap;

            // 頂点座標を設定
            var usdVertices = new VtVec3fArray();
            var usdNormals = new VtVec3fArray();
            var usdBoneWeights = new VtFloatArray();
            var usdBoneIndices = new VtIntArray();
            // rootBoneから対象のボーンまでのパス 先頭に/はつけない
            var usdBones = new VtTokenArray();
            foreach (var joint in sourceBoneInfo)
            {
                sourceBoneIndexMap.Keys.ToList();
                sourceBoneIndexMap.Values.ToList();

                var parentIndex = joint.ParentIndex;
                var path = joint.Name;
            }

            // ボーンウェイトの要素数（最大4など）を取得
            int elementSize = sourceVertices.Max(list => list.Infs.BoneWeight.Length);

            foreach (var vertex in sourceVertices)
            {
                // --- 頂点座標を取得 ---
                var uePos = vertex.Pos;
                // --- 法線 3つ目が法線 --- 
                var ueNormal = vertex.Normal[2]; // 法線取得
                // --- ボーンウェイト ---
                for (int i = 0; i < elementSize; i++)
                {
                    var w = vertex.Infs.BoneWeight[i] / 255.0f;
                    usdBoneWeights.push_back(w);
                    usdBoneIndices.push_back(vertex.Infs.BoneIndex[i]);
                }

                // --- スケール変換 (単位を cm -> m に) ---
                uePos *= 0.01f;
                // 法線は方向ベクトルなので、スケール変換は不要。


                // --- 座標系の変換 (UE -> USD) ---
                // ルール: USD(x, y, z) = UE(y, z, -x)

                // 頂点座標の変換
                var usdPosX = uePos.Y; // USDのXはUEのY
                var usdPosY = uePos.Z; // USDのYはUEのZ
                var usdPosZ = -uePos.X; // USDのZはUEの-X

                // 2) USD 空間で +90° Y 回転 (x' = z, z' = -x)
                float t = usdPosX;
                usdPosX = usdPosZ;    // x' = z
                usdPosZ = -t;         // z' = -x

                // 法線の変換 (頂点座標と同じルールを適用します)
                var usdNormalX = ueNormal.Y;
                var usdNormalY = ueNormal.Z;
                var usdNormalZ = -ueNormal.X;

                // 法線：同じ順でマップ→回転→正規化
                var nX = ueNormal.Y;
                var nY = ueNormal.Z;
                var nZ = -ueNormal.X;
                float nt = nX;
                nX = nZ;   // x' = z
                nZ = -nt;  // z' = -x
                var normalVec = Vector3.Normalize(new Vector3(nX, nY, nZ));

                // --- USD 空間で Y 軸 180° 回転（追加） ---
                // 回転 (x, y, z) -> (-x, y, -z)
                usdPosX = -usdPosX;
                usdPosZ = -usdPosZ;

                // 法線も同じ回転（既に normalVec を作っている場合）
                normalVec = new Vector3(-normalVec.X, normalVec.Y, -normalVec.Z);
                normalVec = Vector3.Normalize(normalVec);


                // --- USDの配列に変換後のデータを追加 ---
                usdVertices.push_back(new GfVec3f(usdPosX, usdPosY, usdPosZ));
                //usdNormals.push_back(new GfVec3f(usdNormalX, usdNormalY, usdNormalZ));
                usdNormals.push_back(new GfVec3f(normalVec.X, normalVec.Y, normalVec.Z));
            }
            usdMesh.CreatePointsAttr().Set(usdVertices);
            usdMesh.CreateNormalsAttr().Set(usdNormals);
            // 法線の補間タイプを「vertex」（頂点単位）に設定（スムーズシェーディングに必要）
            usdMesh.SetNormalsInterpolation(new TfToken("vertex"));


            // 面ごとの頂点インデックスを設定（三角形リストをフラット化）            
            var faceVertexIndices = new VtIntArray();
            foreach (var index in sourceIndices)
            {
                faceVertexIndices.push_back((int)index);
            }            
            usdMesh.CreateFaceVertexIndicesAttr().Set(faceVertexIndices);

            // 面ごとの頂点数を設定（すべて三角形なので3）
            uint triangleCount = (uint)(sourceIndices.Length / 3);
            var faceVertexCounts = new VtIntArray();
            for (int i = 0; i < triangleCount; i++)
            {
                faceVertexCounts.push_back(3);
            }
            usdMesh.CreateFaceVertexCountsAttr().Set(faceVertexCounts);

            // BoneWeight
            var usdSkin = UsdSkelBindingAPI.Apply(usdMesh.GetPrim());
            var usdJointWeightsAttr = usdSkin.CreateJointWeightsAttr(usdBoneWeights);
            usdJointWeightsAttr.SetMetadata(new TfToken("elementSize"), elementSize);
            usdJointWeightsAttr.SetMetadata(new TfToken("interpolation"), "vertex");
            var usdJointIndicesAttr = usdSkin.CreateJointIndicesAttr(usdBoneIndices);
            usdJointIndicesAttr.SetMetadata(new TfToken("elementSize"), elementSize);
            usdJointIndicesAttr.SetMetadata(new TfToken("interpolation"), "vertex");

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
                                ExportMaterialTextures(materialInstance, outputDirectory);
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

            // USDステージを保存
            stage.GetRootLayer().Save();
        }
    }

    /// <summary>
    /// マテリアルインスタンスからテクスチャを抽出し、PNGファイルとしてエクスポートします。
    /// </summary>
    private static void ExportMaterialTextures(UMaterialInstanceConstant materialInstance, string outputDirectory)
    {
        if (materialInstance == null) return;

        var materialDirectoryPath = Path.Combine(outputDirectory, SanitizeUsdName(materialInstance.Name ?? "Material"));
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

                // バーチャルテクスチャはスキップ
                if (texture2D.PlatformData?.VTData != null) continue;

                var mipMap = texture2D.GetFirstMip();
                if (mipMap?.BulkData?.Data == null || mipMap.SizeX == 0 || mipMap.SizeY == 0) continue;

                var compressedData = mipMap.BulkData.Data;
                int width = mipMap.SizeX;
                int height = mipMap.SizeY;

                byte[] decodedPixelData = null;

                switch (texture2D.Format)
                {
                    case EPixelFormat.PF_B8G8R8A8:
                    case EPixelFormat.PF_G8:
                        decodedPixelData = compressedData; // 既に非圧縮
                        break;
                    case EPixelFormat.PF_FloatRGBA:
                        if (texture2D.IsHDR)
                        {
                            decodedPixelData = compressedData; // 既に非圧縮
                        }
                        break;
                    case EPixelFormat.PF_DXT1:
                        decodedPixelData = DecodeToBgra(decoder, compressedData, width, height, CompressionFormat.Bc1);
                        break;

                    case EPixelFormat.PF_DXT5:
                        decodedPixelData = DecodeToBgra(decoder, compressedData, width, height, CompressionFormat.Bc3);
                        break;

                    case EPixelFormat.PF_BC5:
                        decodedPixelData = DecodeToBgra(decoder, compressedData, width, height, CompressionFormat.Bc5);
                        // 法線マップの場合、Zチャンネル（青）を再構築
                        if (texture2D.IsNormalMap && decodedPixelData != null)
                        {
                            ReconstructNormalMapZChannel(decodedPixelData, width, height);
                        }
                        break;

                    case EPixelFormat.PF_BC7:
                        decodedPixelData = DecodeToBgra(decoder, compressedData, width, height, CompressionFormat.Bc7);
                        break;

                    default:
                        // サポート外のフォーマットはスキップ
                        continue;
                }

                if (decodedPixelData == null) continue;

                if (texture2D.IsTextureCube)
                {
                    continue;
                }


                var outputFilePath = Path.Combine(materialDirectoryPath, texture2D.Name);

                if (texture2D.IsHDR)
                {
                    try
                    {
                        outputFilePath += ".hdr";
                        using (var image = Image.LoadPixelData<RgbaVector>(decodedPixelData, width, height))
                        {
                            image.SaveAsPng(outputFilePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to save texture '{texture2D.Name}' to '{outputFilePath}': {ex.Message}");
                    }                    
                }
                else
                {
                    try
                    {
                        outputFilePath += ".png";
                        using (var image = Image.LoadPixelData<Bgra32>(decodedPixelData, width, height))
                        {
                            image.SaveAsPng(outputFilePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to save texture '{texture2D.Name}' to '{outputFilePath}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                // 個別のテクスチャ処理でエラーが発生しても、他のテクスチャの処理を続行
                Console.WriteLine($"Warning: Failed to export a texture parameter. {ex.Message}");
            }
        }
    }

    /// <summary>
    /// BCn圧縮されたピクセルデータをBGRA32形式のバイト配列にデコードします。
    /// </summary>
    private static byte[] DecodeToBgra(BcDecoder decoder, byte[] compressedData, int width, int height, CompressionFormat compressionFormat)
    {
        if (compressedData == null || compressedData.Length == 0) return null;
        try
        {
            var decodedPixels = decoder.DecodeRaw(compressedData, width, height, compressionFormat);
            if (decodedPixels == null || decodedPixels.Length == 0) return null;

            var bgraPixelData = new byte[width * height * 4];
            for (int i = 0; i < decodedPixels.Length; i++)
            {
                var pixel = decodedPixels[i];
                int offset = i * 4;
                bgraPixelData[offset + 0] = pixel.b; // Blue
                bgraPixelData[offset + 1] = pixel.g; // Green
                bgraPixelData[offset + 2] = pixel.r; // Red
                bgraPixelData[offset + 3] = pixel.a; // Alpha
            }
            return bgraPixelData;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during texture decoding with format {compressionFormat}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 2チャンネルの法線マップ（R=X, G=Y）からZ成分を再構築し、Bチャンネルに書き込みます。
    /// </summary>
    private static void ReconstructNormalMapZChannel(byte[] bgraPixelData, int width, int height)
    {
        int pixelCount = width * height;
        for (int i = 0; i < pixelCount; i++)
        {
            int offset = i * 4;
            // [0, 255] の範囲を [-1, 1] の範囲に正規化
            double normalX = (bgraPixelData[offset + 2] / 255.0) * 2.0 - 1.0; // Red channel
            double normalY = (bgraPixelData[offset + 1] / 255.0) * 2.0 - 1.0; // Green channel

            // z^2 = 1 - x^2 - y^2
            double zSquared = 1.0 - normalX * normalX - normalY * normalY;
            double normalZ = Math.Sqrt(Math.Max(0.0, zSquared));

            // [-1, 1] の範囲を [0, 255] の範囲に戻してBチャンネルに格納
            bgraPixelData[offset + 0] = (byte)(Math.Clamp(normalZ, 0.0, 1.0) * 255.0); // Blue channel
        }
    }

    /// <summary>
    /// Unreal Engine の FMatrix に似た構造のオブジェクトを USD の GfMatrix4d に変換します。
    /// </summary>
    /// <remarks>
    /// CUE4ParseのFMatrixが直接参照できない場合を想定し、dynamic型を使用しています。
    /// </remarks>
    private static GfMatrix4d ConvertFMatrixToMatrix4d(dynamic ueMatrix)
    {
        var resultMatrix = new GfMatrix4d(); // デフォルトで単位行列
        try
        {
            // 行優先で値を設定
            //resultMatrix[0, 0] = ueMatrix.M[0][0]; resultMatrix[0, 1] = ueMatrix.M[0][1]; resultMatrix[0, 2] = ueMatrix.M[0][2]; resultMatrix[0, 3] = ueMatrix.M[0][3];
            //resultMatrix[1, 0] = ueMatrix.M[1][0]; resultMatrix[1, 1] = ueMatrix.M[1][1]; resultMatrix[1, 2] = ueMatrix.M[1][2]; resultMatrix[1, 3] = ueMatrix.M[1][3];
            //resultMatrix[2, 0] = ueMatrix.M[2][0]; resultMatrix[2, 1] = ueMatrix.M[2][1]; resultMatrix[2, 2] = ueMatrix.M[2][2]; resultMatrix[2, 3] = ueMatrix.M[2][3];
            //resultMatrix[3, 0] = ueMatrix.M[3][0]; resultMatrix[3, 1] = ueMatrix.M[3][1]; resultMatrix[3, 2] = ueMatrix.M[3][2]; resultMatrix[3, 3] = ueMatrix.M[3][3];
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to convert FMatrix to GfMatrix4d, returning identity matrix. {ex.Message}");
            // 失敗した場合は単位行列を返す
        }
        return resultMatrix;
    }
}