

using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Animation.ACL;
using CUE4Parse.UE4.Assets.Exports.Animation.DeformerGraph;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.NavigationSystem;
using CUE4Parse.UE4.Objects.PhysicsEngine;
using CUE4Parse.UE4.Objects.RigVM;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json.Linq;
using Org.BouncyCastle.Asn1.Cms;
using pxr;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Numerics;
using System.Text;
using static System.Collections.Specialized.BitVector32;

/// <summary>
/// CUE4Parse 1.22
/// nuget で CUE4Parse をインストールして利用する
/// 
/// 
/// https://github.com/Dmgvol/UE_Modding/blob/main/README.md
/// https://unofficial-modding-guide.com/posts/thebasics/
/// 
/// 
/// nuget で UniversalSceneDescription 6.0
/// USD v23.02?
/// プリム名にutf8使えない
/// USD 24.03 以降サポートされてる
/// 
/// https://github.com/CanTalat-Yakan/USD.NET
/// 
/// 
/// 
/// bmp でテクスチャ保存してる
/// SixLabors.ImageSharp
/// デコードするのに
/// BCnEncoder.Net
/// 
/// </summary>



public class USkeletalMeshToUSD
{
    const string AttrOriginalName = "UEOriginalName";

    private static string SanitizeUsdName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "Unnamed";

        // 半角英数字とアンダースコアのみ許可
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if ((c >= 'a' && c <= 'z') ||
                (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') ||
                c == '_')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('_'); // 不正文字はアンダースコアに置換
            }
        }

        // Prim名は数字で始められない → '_' で補正
        if (sb.Length > 0 && char.IsDigit(sb[0]))
            sb.Insert(0, '_');

        return sb.ToString();
    }

    public static void Convert(USkeletalMesh skeletalMesh, string outputPath)
    {
        var _orignalName = skeletalMesh.Name;
        var _primName = SanitizeUsdName(_orignalName);

        // USDステージを作成
        UsdStage stage = UsdStage.CreateNew(outputPath + _primName + ".usda");
        if (stage == null)
        {
            return;
        }


        UsdGeomScope scope = UsdGeomScope.Define(stage, "/" + "Geo");



        UsdGeomMesh usdMesh = UsdGeomMesh.Define(stage, scope.GetPath() + "/" + _primName);
        usdMesh.GetPrim().CreateAttribute(new TfToken(AttrOriginalName), Sdf.SdfGetValueTypeString()).Set(_orignalName);



        // Vertex
        var sourceVerts = skeletalMesh.LODModels[0].VertexBufferGPUSkin.VertsFloat;
        var sourceIndices = skeletalMesh.LODModels[0].Indices.Indices16;


        // ToArrayメソッドでそれを配列に変換します。
        Vector3[] vertices = sourceVerts.Select(fv => new Vector3(fv.Pos.X, fv.Pos.Y, fv.Pos.Z)).ToArray();
        VtVec3fArray usdVertices = new VtVec3fArray();

        // 回転行列の作成 (rotationAnglesが0,0,0の場合は単位行列)
        //Matrix4x4 rotationMatrix = (rotationAngles == Vector3.zero) ? Matrix4x4.identity : Matrix4x4.Rotate(Quaternion.Euler(rotationAngles));

        foreach (var v in vertices)
        {
            //Vector3 vec = rotationMatrix.MultiplyPoint(v);
            // 左手座標系から右手座標系に変換 Xを反転
            usdVertices.push_back(new pxr.GfVec3f(-v.X, v.Y, v.Z));
        }
        usdMesh.CreatePointsAttr().Set(usdVertices);


        

        VtIntArray indices = new VtIntArray();
        foreach (var index in sourceIndices)
        {
            indices.push_back(index);
        }
        usdMesh.CreateFaceVertexIndicesAttr().Set(indices);


        //面の頂点数の配列 三角ポリゴンの場合3,3,3,3... 四角ポリゴンの場合4,4,4,4,4... 
        VtIntArray faceVertexCounts = new VtIntArray();
        int numFaces = sourceIndices.Length / 3; // 三角形ポリゴンの場合
        for (int i = 0; i < numFaces; i++)
        {
            faceVertexCounts.push_back(3); // 三角形ポリゴンなので各面の頂点数は3
        }
        usdMesh.CreateFaceVertexCountsAttr().Set(faceVertexCounts);

        //indices = new int[usdFaceData.FaceVertexIndices.size()];
        //usdFaceData.FaceVertexIndices.CopyToArray(indices);

        var mat = skeletalMesh.Materials;
        var mats = skeletalMesh.SkeletalMaterials;

        // Subset
        var sections = skeletalMesh.LODModels[0].Sections;
        if (sections.Length > 1)
        {
            int faceIndexOffset = 0; // 累積フェース数を追跡する変数
            foreach (var section in sections)
            {
                var _matSlotName = mats[section.MaterialIndex].MaterialSlotName.Text;
                string subsetName = SanitizeUsdName(_matSlotName);


                var subsetFaceIndices = new VtIntArray((uint)section.NumTriangles);

                // フェースインデックスを生成して追加する
                for (int i = 0; i < section.NumTriangles; i++)
                {
                    // faceIndexOffset からセクション内の三角形の数だけ連番を追加
                    subsetFaceIndices.push_back(faceIndexOffset + i);
                }


                // サブメッシュ用の USD Subset を作成
                var subset = UsdGeomSubset.CreateGeomSubset(
                    usdMesh,
                    new TfToken(subsetName),
                    new TfToken(UsdGeomTokens.face), // UsdGeomTokensを使うとタイプセーフ
                    subsetFaceIndices,
                    new TfToken("materialBind") // familyName
                );

                faceIndexOffset += (int)section.NumTriangles;

                subset.GetPrim().CreateAttribute(new TfToken(AttrOriginalName), Sdf.SdfGetValueTypeString()).Set(_matSlotName);


                // マテリアルバインディング

                if (mat[section.MaterialIndex] == null)
                {
                    continue;
                }
                var matpath = mat[section.MaterialIndex].GetPathName();
                mat[section.MaterialIndex].TryLoad(out UObject export);
                if (export is UMaterialInstanceConstant mic)
                {
                    // マテリアル名のディレクトリを作成
                    string materialDirectory = Path.Combine(outputPath, mic.Name);
                    Directory.CreateDirectory(materialDirectory);

                    foreach (var param in mic.TextureParameterValues)
                    {
                        if (param.ParameterValue.ResolvedObject == null)
                        {
                            continue;
                        }

                        byte[] compressedData;
                        int width = 0;
                        int height = 0;
                        var decoder = new BcDecoder();
                        ColorRgba32[] colorRgba32 = null;
                        // ピクセルフォーマットに応じて処理を分岐
                        EPixelFormat pixelFormat;

                        param.ParameterValue.ResolvedObject.TryLoad(out UObject exporttex);
                        if (exporttex is UTexture2D utex)
                        {
                            // Virtual Textureの場合はスキップ（元のロジックを維持）
                            if (utex.PlatformData?.VTData != null)
                            {
                                continue;

                                compressedData = utex.PlatformData.VTData.Chunks[0].BulkData.Data;
                                width = (int)utex.PlatformData.VTData.Width;
                                height = (int)utex.PlatformData.VTData.Height;
                                pixelFormat = utex.PlatformData.VTData.LayerTypes[0];


                                switch (pixelFormat)
                                {
                                    // 非圧縮BGRA (元のコードで対応していた形式)
                                    case EPixelFormat.PF_B8G8R8A8:
                                        break;

                                    // DXT1 / BC1 (RGB, 1bit Alpha)
                                    case EPixelFormat.PF_DXT1: // UAssetAPIのバージョンによってはenum名が違う可能性
                                        colorRgba32 = decoder.DecodeRaw(compressedData, width, height, BCnEncoder.Shared.CompressionFormat.Bc1);
                                        break;

                                    // DXT5 / BC3 (RGBA) - 今回のエラーの原因である可能性が最も高い形式
                                    case EPixelFormat.PF_DXT5:
                                        colorRgba32 = decoder.DecodeRaw(compressedData, width, height, BCnEncoder.Shared.CompressionFormat.Bc3);
                                        break;

                                    // BC5 (Normal Mapなどで使われる2チャンネル形式)
                                    case EPixelFormat.PF_BC5:
                                        colorRgba32 = decoder.DecodeRaw(compressedData, width, height, BCnEncoder.Shared.CompressionFormat.Bc5);
                                        break;

                                    // BC5 (Normal Mapなどで使われる2チャンネル形式)
                                    case EPixelFormat.PF_BC7:
                                        colorRgba32 = decoder.DecodeRaw(compressedData, width, height, BCnEncoder.Shared.CompressionFormat.Bc7);
                                        break;

                                    // 他のフォーマットは未対応としてスキップ
                                    default:
                                        Console.WriteLine($"Skipping texture '{utex.Name}' due to unsupported pixel format: {pixelFormat}");
                                        continue; // switchを抜けて次のテクスチャへ
                                }

                                // 必要であればVTのデータ処理をここに追加
                                continue;
                            }

                            // 最初のミップマップを取得
                            var mip = utex.GetFirstMip();
                            if (mip == null || mip.BulkData.Data == null)
                            {
                                continue; // データがなければスキップ
                            }

                            compressedData = mip.BulkData.Data;
                            width = mip.SizeX;
                            height = mip.SizeY;

                            // 幅や高さが0、またはデータがない場合は処理しない
                            if (width <= 0 || height <= 0 || compressedData.Length == 0)
                            {
                                continue;
                            }

                            // 非圧縮のピクセルデータを格納するための変数
                            byte[] decodedData = null;

                            pixelFormat = utex.Format;

                            switch (pixelFormat)
                            {
                                // 非圧縮BGRA (元のコードで対応していた形式)
                                case EPixelFormat.PF_B8G8R8A8:
                                case EPixelFormat.PF_G8:
                                    decodedData = compressedData; // そのまま使える
                                    break;

                                // DXT1 / BC1 (RGB, 1bit Alpha)
                                case EPixelFormat.PF_DXT1:
                                    colorRgba32 = decoder.DecodeRaw(compressedData, width, height, BCnEncoder.Shared.CompressionFormat.Bc1);
                                    break;

                                // DXT5 / BC3 (RGBA) 
                                case EPixelFormat.PF_DXT5:
                                    colorRgba32 = decoder.DecodeRaw(compressedData, width, height, BCnEncoder.Shared.CompressionFormat.Bc3);
                                    break;

                                // BC5 (Normal Mapなどで使われる2チャンネル形式)
                                case EPixelFormat.PF_BC5:
                                    colorRgba32 = decoder.DecodeRaw(compressedData, width, height, BCnEncoder.Shared.CompressionFormat.Bc5);
                                    if (utex.IsNormalMap)
                                    {
                                        // 配列の各ピクセルに対してループ処理
                                        for (int i = 0; i < colorRgba32.Length; i++)
                                        {
                                            // i番目のピクセルを取得
                                            // pixels[i] は ColorRgba32 構造体で、R, G, B, A のプロパティを持つ
                                            ColorRgba32 pixel = colorRgba32[i];

                                            // RとGの値を取得
                                            byte r = pixel.r;
                                            byte g = pixel.g;

                                            // 0-255の範囲を-1.0から1.0の範囲に変換
                                            double x = (r / 255.0) * 2.0 - 1.0;
                                            double y = (g / 255.0) * 2.0 - 1.0;

                                            // Z成分を計算: z = sqrt(1 - x^2 - y^2)
                                            double z_squared = 1.0 - x * x - y * y;
                                            double z = Math.Sqrt(Math.Max(0.0, z_squared));

                                            // 計算したZ成分を0-255の範囲に戻す
                                            byte newB = (byte)(z * 255.0);

                                            // 配列内のピクセルのB値を直接更新します
                                            colorRgba32[i].b = newB;
                                        }
                                    }

                                    break;

                                case EPixelFormat.PF_BC7:
                                    colorRgba32 = decoder.DecodeRaw(compressedData, width, height, BCnEncoder.Shared.CompressionFormat.Bc7);
                                    break;

                                // 他のフォーマットは未対応としてスキップ
                                default:
                                    Console.WriteLine($"Skipping texture '{utex.Name}' due to unsupported pixel format: {pixelFormat}");
                                    continue; // switchを抜けて次のテクスチャへ
                            }

                            // デコードに失敗したか、データがない場合はスキップ
                            if (decodedData == null)
                            {
                                decodedData = new byte[width * height * 4];
                                for (int i = 0; i < width * height; i++)
                                {
                                    var pixel = colorRgba32[i]; // i番目のピクセルを取得

                                    // ImageSharpのBgra32に合わせて、B, G, R, A の順でバイト配列に格納
                                    decodedData[i * 4 + 0] = pixel.b; // Blue
                                    decodedData[i * 4 + 1] = pixel.g; // Green
                                    decodedData[i * 4 + 2] = pixel.r; // Red
                                    decodedData[i * 4 + 3] = pixel.a; // Alpha
                                }
                            }

                            // 出力ファイルパスを生成
                            string outputFilePath = Path.Combine(materialDirectory, utex.Name + ".png");

                            try
                            {
                                // Unreal Engineのピクセルフォーマットが PF_B8G8R8A8 であることを前提とする
                                // このフォーマットは ImageSharp の Bgra32 に対応します
                                // ImageSharpを使ってピクセルデータから画像オブジェクトを生成
                                using (var image = Image.LoadPixelData<Bgra32>(decodedData, width, height))
                                {
                                    // BMPファイルとして保存
                                    image.SaveAsPng(outputFilePath);
                                }
                            }
                            catch (Exception ex)
                            {
                                // 保存中にエラーが発生した場合の処理
                                Console.WriteLine($"Failed to save texture '{utex.Name}': {ex.Message}");
                            }
                        }
                    }
                }

            }
        }
        


        //usdMesh.CreatePointsAttr().Set(points);
        // ステージを保存
        stage.GetRootLayer().Save();

        return;
    }
    private static GfMatrix4d ConvertFMatrixToMatrix4d(FMatrix matrix)
    {
        // FMatrixをGfMatrix4dに変換
        // FMatrixはUnrealのマトリックス、行メジャーか確認
        GfMatrix4d mat = new GfMatrix4d();
        // 要素を設定
        // mat.SetRow(0, new GfVec4d(matrix.M[0][0], matrix.M[0][1], matrix.M[0][2], matrix.M[0][3]));
        // など
        return mat;
    }
}

internal class Program
{
    static void Main(string[] args)
    {
        // 1. ゲームのインストールフォルダとUE Version を指定して、プロバイダーを初期化する
        // C:\Program Files (x86)\Steam\steamapps\common\MGSDelta\MGSDelta\Content\Paks
        var provider = new DefaultFileProvider(@"H:\Paks\mgs3", SearchOption.AllDirectories, true, new VersionContainer(EGame.GAME_UE5_3));

        // 2. MappingFileを設定
        provider.MappingsContainer = new FileUsmapTypeMappingsProvider("H:\\Paks\\mgs3\\5.3.2-1582552+++rg5+rel_1.1.1-MGSDelta.usmap");

        // 3. プロバイダーを初期化して、.pak ファイルを読み込ませる
        provider.Initialize();

        // 4. AES Key 暗号化されていない場合は0でいい
        provider.SubmitKey(new FGuid(), new FAesKey("0x0000000000000000000000000000000000000000000000000000000000000000"));

        // 5. OodleHelper.Initializeでライブラリを指定するとOodleで圧縮されたファイルにアクセスできるようになる
        // DL OodleDll
        OodleHelper.DownloadOodleDll("oo2core_9_win64.dll");
        OodleHelper.Initialize("oo2core_9_win64.dll");


        //var dir = "USkeletalMesh";
        var dir = @"r:\testgeo\USkeletalMesh";
        Directory.CreateDirectory(dir);


        //var files = provider.Files.Values.Where(x => x.Path.Contains("Mesaru"));
        var files = provider.Files.Values.Where(x => x.Path.Contains("SKM_"));
        //var files = provider.Files.Values.Where(x => x.Path.Contains("SM_"));
        foreach (var file in files)
        {
            // uasset umap のパスを指定 圧縮されてない状態でないと失敗する
            provider.TryLoadPackage(file.Path, out IPackage pkg);
            if (pkg == null)
                continue;

            foreach (var export in pkg.GetExports())
            {
                var t = export.GetType();
                var tc = t.Name;


                if (export is UStaticMesh obj6)
                {
                    var StaticMaterials = obj6.Properties[0].Name;

                    //obj6.Properties[0].Tag;
                    //obj6.Outer.Owner.NameMap
                    //obj6.Outer

                    //var test = ((CUE4Parse.UE4.Assets.IoPackage)obj6.Outer).ImportedPackages.Value[0];
                    //var namemap = test.NameMap;
                    //obj6.Properties[0].Name
                    if (obj6.RenderData == null)
                        continue;

                    var v = obj6.RenderData.LODs[0].VertexBuffer;
                    continue;
                }
                if (export is USkeletalMesh obj33)
                {
                    foreach (var lod in obj33.LODModels)
                    {
                        USkeletalMeshToUSD.Convert(obj33, dir + "\\");

                        break;
                        continue;






                        if (lod.Chunks.Length > 0)
                        {
                            var chunk = lod.Chunks[0];
                        }
                        var name = dir + "\\" + obj33.Name;
                        string content = "g\n";
                        foreach (var item in lod.VertexBufferGPUSkin.VertsFloat)
                        {
                            content += "v ";
                            content += item.Pos.X.ToString() + " ";
                            content += item.Pos.Y.ToString() + " ";
                            content += item.Pos.Z.ToString() + "\n";

                        }

                        File.WriteAllText(name + ".obj", content);

                        break;
                        continue;
                    }

                    continue;
                }



                if (export is USoundBase objx)
                {
                    continue;
                }

                if (export is UTexture2D obj)
                {
                    continue;
                }
                if (export is UMaterial obj0)
                {
                    continue;
                }
                if (export is UMediaTexture obj1)
                {
                    continue;
                }
                if (export is UMaterialInstanceConstant obj4)
                {
                    continue;
                }

                //
                if (tc == "UObject")
                {
                    continue;
                }


                if (export is UObject obj50)
                {
                    continue;
                }

            }
        }


    }
}

