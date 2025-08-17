using MelonLoader;
using UnityEngine;
using K9_Patrol;

namespace TemplateUtils
{
    public static class AssetBundleUtils
    {
        private static readonly Core mod = MelonAssembly.FindMelonInstance<Core>();
        private static readonly MelonAssembly melonAssembly = mod.MelonAssembly;

        public static
#if IL2CPP
            Il2CppAssetBundle
#elif MONO
            AssetBundle
#endif
            LoadAssetBundle(string bundleFileName)
        {
            try
            {
                string streamPath = bundleFileName;
                Stream bundleStream = melonAssembly.Assembly.GetManifestResourceStream($"{streamPath}");
                if (bundleStream == null)
                {
                    mod.Unregister($"AssetBundle: '{streamPath}' not found. \nOpen .csproj file and search for '{bundleFileName}'.\nIf it doesn't exist,\nCopy your asset to Assets/ folder then look for 'your.assetbundle' in .csproj file.");
                    return null;
                }
#if IL2CPP
                byte[] bundleData;
                using (MemoryStream ms = new())
                {
                    bundleStream.CopyTo(ms);
                    bundleData = ms.ToArray();
                }
                Il2CppSystem.IO.Stream stream = new Il2CppSystem.IO.MemoryStream(bundleData);
                return Il2CppAssetBundleManager.LoadFromStream(stream);
#elif MONO
                return AssetBundle.LoadFromStream(bundleStream);
#endif
            }
            catch (Exception e)
            {
                mod.Unregister($"Failed to load AssetBundle. Please report to dev: {e}");
                return null;
            }
        }

        public static
#if IL2CPP
            Il2CppAssetBundle
#elif MONO
            AssetBundle
#endif
            GetLoadedAssetBundle(string asset_name_flag)
        {
#if IL2CPP

            Il2CppAssetBundle[] loadedBundles = Il2CppAssetBundleManager.GetAllLoadedAssetBundles();
#elif MONO
            AssetBundle[] loadedBundles = AssetBundle.GetAllLoadedAssetBundles().ToArray();
#endif
            try
            {
                foreach (var bundle in loadedBundles)
                {
                    if (bundle.Contains(asset_name_flag)) return bundle;
                }
                string assetNames = "";
                foreach (var bundle in loadedBundles)
                {
                    string[] bundleAssetNames = bundle.GetAllAssetNames();
                    string bundleAssetNamesString = string.Join("\n\r -", bundleAssetNames);
                    assetNames +=
#if IL2CPP
                        bundle
#elif MONO
                        bundle.name
#endif
                        + $"({bundleAssetNames.Length} assets):" + bundleAssetNamesString;
                }
                throw new Exception($"Asset '{asset_name_flag}' not found in {loadedBundles.Length} bundle(s).\n{assetNames}");
            }
            catch (Exception e)
            {
                mod.Unregister($"Failed to get loaded AssetBundle. Please report to dev: \n{e}");
                return null;
            }

        }

        public static GameObject LoadAssetFromBundle(string asset_name)
        {
            var bundle = GetLoadedAssetBundle(asset_name);
            return bundle.LoadAsset<GameObject>(asset_name);
        }
    }
}
