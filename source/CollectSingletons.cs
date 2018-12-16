﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using Harmony;
using BattleTech;
using BattleTech.Data;
using BattleTech.Assetbundles;
using static BattletechPerformanceFix.Extensions;

namespace BattletechPerformanceFix
{
    class CollectSingletons : Feature
    {
        public static DataManager DM;
        public static TextureManager TM;
        public static PrefabCache PC;
        public static SpriteCache SC;
        public static SVGCache SVC;
        public static BattleTechResourceLocator RL;
        public static AssetBundleManager BM;
        public static MessageCenter MC;


        public static bool Initialized = false;
        public static void SetUnityDataManagers_Post(DataManager __instance) {
            if (Initialized) return;
            Initialized = true;
            DM = __instance;
            var t = new Traverse(__instance);
            
            T f<T>(string name) {
                return Trap(() => t.Property(name).GetValue<T>());
            }

            TM = f<TextureManager>("TextureManager");
            TM = f<TextureManager>("TextureManager");
            PC = f<PrefabCache>("GameObjectPool");
            SC = f<SpriteCache>("SpriteCache");
            SVC = f<SVGCache>("SVGCache");
            RL = DM.ResourceLocator;
            BM = f<AssetBundleManager>("AssetBundleManager");
            MC = f<MessageCenter>("MessageCenter");

            MC.AddSubscriber( MessageCenterMessageType.DataManagerLoadCompleteMessage
                            , _ => Spam(() => "DataManagerLoadComplete"));

            void PingQueue() {
                WaitAFrame(240)
                        .Done(() => {
                                var dmlr = new Traverse(__instance).Field("foregroundRequestsList").GetValue<List<DataManager.DataManagerLoadRequest>>();
                                var byid = string.Join(" ", dmlr.Select(lr => $"{lr.ResourceId}:{lr.ResourceType.ToString()}[{lr.State}]").Take(10).ToArray());
                                LogDebug($"ProcessRequests :10waiting [{byid}]"); // from {new StackTrace().ToString()}");

                                PingQueue();
                            });
                }

            PingQueue();

            "ProcessRequests".Pre<DataManager>();
        }

        public static void ProcessRequests_Pre(DataManager __instance) {
            var fromMethod = new StackFrame(2).GetMethod();
            var isFromExternal = fromMethod.DeclaringType.Name != "DataManager";

            if (!isFromExternal) return;

            var dmlr = new Traverse(__instance).Field("foregroundRequestsList").GetValue<List<DataManager.DataManagerLoadRequest>>();
            if (dmlr.Count > 0) {
                var byid = string.Join(" ", dmlr.Select(lr => $"{lr.ResourceId}:{lr.ResourceType.ToString()}").Take(10).ToArray());
                LogDebug($"ProcessRequests started with: {byid}");
            } else {
                LogDebug($"ProcessRequests[external? {isFromExternal}] started with an EMPTY queue from {fromMethod.DeclaringType.FullName}.{fromMethod.Name} this will never complete!");
                CollectSingletons.MC.PublishMessage(new DataManagerLoadCompleteMessage());
            }
        }


        public void Activate() {
            "SetUnityDataManagers".Post<DataManager>();
        }
    }
}
