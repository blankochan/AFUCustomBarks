using HarmonyLib;
using Il2Cpp;
using Il2CppCustomCharacters;
using Il2CppPhoton.Client;
using Il2CppPhoton.Realtime;
using Il2CppUI_Localization;
using MelonLoader;
using System.Collections;
using UnityEngine;
using static Il2CppUI_Localization.BarksLibrary;

[assembly: MelonInfo(typeof(CustomBarks.Core), "CustomBarks", "1.0.0", "Azore", null)]
[assembly: MelonGame("Videocult", "Airframe")]

namespace CustomBarks
{
    public class Core : MelonMod
    {
        private static bool barksInjected = false;
        private static List<KeyValuePair<string, string>> localCustomBarks = new List<KeyValuePair<string, string>>();
        private static string customBarksFolderPath;
        private static int originalBarksCount = -1;

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("Custom Barks Mod active...");

            customBarksFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Custom Barks");
            if (!Directory.Exists(customBarksFolderPath))
            {
                Directory.CreateDirectory(customBarksFolderPath);
                string examplePath = Path.Combine(customBarksFolderPath, "example_pack.txt");
                File.WriteAllText(examplePath,
                    "//This is an example of a Bark File. They are written in bark_key=BarkText. New line = new bark." + Environment.NewLine +
                    "backrooms_1=And the best part is..." + Environment.NewLine +
                    "backrooms_2=...you can eat them!");
            }

            PreloadLocalBarks();
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            barksInjected = false;
            MelonCoroutines.Start(DelayedSyncPipeline());
        }

        private static IEnumerator DelayedSyncPipeline()
        {
            yield return new WaitForSeconds(2.0f);
            SyncBarksWithRoomRegistry();
        }

        private static void PreloadLocalBarks()
        {
            localCustomBarks.Clear();
            try
            {
                string[] packFiles = Directory.GetFiles(customBarksFolderPath, "*.txt");
                foreach (string file in packFiles)
                {
                    string[] lines = File.ReadAllLines(file);
                    foreach (string line in lines)
                    {
                        if (string.IsNullOrEmpty(line.Trim()) || line.StartsWith("//"))
                            continue;

                        int separatorIndex = line.IndexOf('=');
                        if (separatorIndex > 0)
                        {
                            string key = line.Substring(0, separatorIndex).Trim();
                            string text = line.Substring(separatorIndex + 1).Trim();

                            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(text))
                            {
                                localCustomBarks.Add(new KeyValuePair<string, string>(key, text));
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Error loading external barks: {e.Message}");
            }
        }

        public static void SyncBarksWithRoomRegistry()
        {
            try
            {
                PhotonController controllerInstance = null;
                if (PhotonController.instance != null) controllerInstance = PhotonController.instance;

                if (controllerInstance == null)
                {
                    controllerInstance = UnityEngine.Object.FindObjectOfType<PhotonController>();
                }

                if (controllerInstance == null || controllerInstance.client == null || controllerInstance.client.CurrentRoom == null)
                {
                    var offlineBarks = localCustomBarks.OrderBy(b => b.Key).ToList();
                    InjectBarksFromList(offlineBarks);
                    return;
                }

                var client = controllerInstance.client;
                var room = client.CurrentRoom;

                if (client.LocalPlayer != null && client.LocalPlayer.CustomProperties != null)
                {
                    var myProps = client.LocalPlayer.CustomProperties;
                    if (!myProps.ContainsKey("AzoreCustomBarks"))
                    {
                        PhotonHashtable myBarksTable = new PhotonHashtable();
                        for (int i = 0; i < localCustomBarks.Count; i++)
                        {
                            string packedData = $"{localCustomBarks[i].Key}:{localCustomBarks[i].Value}";
                            myBarksTable.Add(i.ToString(), packedData);
                        }

                        PhotonHashtable playerPropsWrapper = new PhotonHashtable();
                        playerPropsWrapper.Add("AzoreCustomBarks", myBarksTable);

                        client.LocalPlayer.SetCustomProperties(playerPropsWrapper);
                        MelonLogger.Msg("barks saved to properties");
                    }
                }

                SortedDictionary<int, List<KeyValuePair<string, string>>> globalNetworkRegistry = new SortedDictionary<int, List<KeyValuePair<string, string>>>();

                if (room.Players != null)
                {
                    var playersEnum = room.Players.GetEnumerator();
                    while (playersEnum.MoveNext())
                    {
                        var playerPair = playersEnum.Current;
                        var playerObj = playerPair.Value;
                        if (playerObj == null || playerObj.CustomProperties == null) continue;

                        int actorId = playerObj.ActorNumber;
                        var playerProps = playerObj.CustomProperties;

                        if (playerProps.ContainsKey("AzoreCustomBarks"))
                        {
                            var playerTable = playerProps["AzoreCustomBarks"].Cast<PhotonHashtable>();
                            if (playerTable == null || playerTable.Keys == null) continue;

                            List<KeyValuePair<string, string>> playerBarksList = new List<KeyValuePair<string, string>>();

                            int barkIndex = 0;
                            while (playerTable.ContainsKey(barkIndex.ToString()))
                            {
                                var valObj = playerTable.System_Collections_IDictionary_get_Item(barkIndex.ToString());
                                if (valObj != null)
                                {
                                    string rawData = valObj.ToString();
                                    int separator = rawData.IndexOf(':');
                                    if (separator > 0)
                                    {
                                        string netKey = rawData.Substring(0, separator);
                                        string netText = rawData.Substring(separator + 1);
                                        playerBarksList.Add(new KeyValuePair<string, string>(netKey, netText));
                                    }
                                }
                                barkIndex++;
                            }

                            globalNetworkRegistry[actorId] = playerBarksList.OrderBy(b => b.Key).ToList();
                        }
                    }
                }

                List<KeyValuePair<string, string>> finalFlatBarksToInject = new List<KeyValuePair<string, string>>();

                foreach (var playerPacket in globalNetworkRegistry)
                {
                    foreach (var bark in playerPacket.Value)
                    {
                        if (!finalFlatBarksToInject.Any(b => b.Key == bark.Key))
                        {
                            finalFlatBarksToInject.Add(bark);
                        }
                    }
                }

                if (finalFlatBarksToInject.Count == 0)
                {
                    finalFlatBarksToInject = localCustomBarks.OrderBy(b => b.Key).ToList();
                }

                InjectBarksFromList(finalFlatBarksToInject);
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Error during player sync: {e.Message}");
            }
        }
        public static void InjectBarksFromList(List<KeyValuePair<string, string>> barksToInject)
        {
            if (barksToInject.Count == 0) return;

            try
            {
                var charLibraries = Resources.FindObjectsOfTypeAll<CustomCharactersLibrary>().ToArray();
                if (charLibraries == null || charLibraries.Length == 0) return;

                foreach (var charLib in charLibraries)
                {
                    if (charLib == null) continue;
                    BarksLibrary lib = charLib.barksLibrary;
                    if (lib == null || lib.barks == null) continue;

                    if (originalBarksCount == -1)
                    {
                        originalBarksCount = lib.barks.Count;
                    }

                    for (int i = lib.barks.Count - 1; i >= originalBarksCount; i--)
                    {
                        if (lib.barks[i] != null && lib.barks[i].name != null && lib.barks[i].name.StartsWith("NetBark_"))
                        {
                            lib.barks.RemoveAt(i);
                        }
                    }

                    int nextIndex = lib.barks.Count;

                    foreach (var barkPair in barksToInject)
                    {
                        BarksLibrary.Bark customBark = new BarksLibrary.Bark();
                        customBark.name = $"NetBark_{nextIndex}";
                        customBark.index = nextIndex;
                        customBark.barkText = barkPair.Value;
                        customBark.key = barkPair.Key;
                        customBark.hidden = false;

                        lib.barks.Add(customBark);
                        nextIndex++;
                    }

                    for (int i = 0; i < lib.barks.Count; i++)
                    {
                        if (lib.barks[i] != null)
                        {
                            lib.barks[i].index = i;
                        }
                    }
                }

                barksInjected = true;
            }
            catch (Exception e)
            {
                MelonLoader.MelonLogger.Error($"evil ass error: {e.Message}");
            }
        }
        [HarmonyPatch(typeof(Il2Cpp.PhotonController), "OnPlayerPropertiesUpdate")]
        public static class QuantumPlayerPropertiesPatch
        {
            public static void Postfix()
            {
                CustomBarks.Core.SyncBarksWithRoomRegistry();
            }
        }

    }
}