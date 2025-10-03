

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
                        USkeletalMeshToUSD.ConvertToSplitUsd(obj33, dir + "\\");
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

