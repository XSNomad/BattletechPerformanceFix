using HBS.Logging;
using Harmony;
using BattleTech;
using BattleTech.UI;
using System.Collections.Generic;
using System.Collections;
using System;
using System.Linq;
using System.Reflection;
using System.Diagnostics;

namespace BattletechPerformanceFix
{
    // Deprecated, will be removed.
    public class DefAndCount {
        public MechComponentRef ComponentRef;
        public int Count;
        public DefAndCount(MechComponentRef componentRef, int count) {
            this.ComponentRef = componentRef;
            this.Count = count;
        }

        public void Decr() {
            if (Count != int.MinValue) Count--;
        }
        public void Incr() {
            if (Count != int.MinValue) Count++;
        }
    }

    /* This patch fixes the slow inventory list creation within the mechlab. Without the fix, it manifests as a very long loadscreen where the indicator is frozen.
       
       The core of the problem is a lack of separation between Data & Visuals.
       Most of the logic requires operating on visual elements, which come from the asset pool (or a prefab if not in pool)
          additionally, the creation or modification of data causes preperation for re-render of the assets. (UpdateTooltips, UpdateDescription, Update....)
    
       Solution:
         Separate the data & visual elements entirely.
         Always process the data first, and then only create or re-use a couple of visual elements to display it.
         The user only sees 8 items at once, and they're expensive to create, so only make 8 of them.
     */
    public class PatchMechlabLimitItems {
        MechLabPanel instance;
        MechLabInventoryWidget inventoryWidget;

        // Deprecated, will be removed.
        List<DefAndCount> inventory;

        List<InventoryItemElement_NotListView> ielCache;

        List<ListElementController_BASE_NotListView> rawInventory;
        List<ListElementController_BASE_NotListView> filteredInventory;

        // Index of current item element at the top of scrollrect
        int index = 0;

        int endIndex = 0;

        // Temporary visual element used in the filter process.
        InventoryItemElement_NotListView iieTmp;

        PatchMechlabLimitItems(MechLabPanel instance) {
            try {
                var sw = new Stopwatch();
                sw.Start();
            this.instance = instance;
            this.inventoryWidget = new Traverse(instance).Field("inventoryWidget").GetValue<MechLabInventoryWidget>();

            if (instance.IsSimGame) {
                new Traverse(instance).Field("originalStorageInventory").SetValue(instance.storageInventory);
            }


            inventory = instance.storageInventory.Select(mcr => {
                mcr.DataManager = instance.dataManager;
                mcr.RefreshComponentDef();
                var num = !instance.IsSimGame ? int.MinValue : instance.sim.GetItemCount(mcr.Def.Description, mcr.Def.GetType(), SimGameState.ItemCountType.UNDAMAGED_ONLY); // Undamaged only is wrong, just for testing.
                return new DefAndCount(mcr, num);
            }).ToList();

            /* Build a list of data only for all components. */
            rawInventory = inventory.Select<DefAndCount, ListElementController_BASE_NotListView>(dac => {
                if (dac.ComponentRef.ComponentDefType == ComponentType.Weapon) {
                    ListElementController_InventoryWeapon_NotListView controller = new ListElementController_InventoryWeapon_NotListView();
                    controller.InitAndFillInSpecificWidget(dac.ComponentRef, null, instance.dataManager, null, dac.Count, false);
                    return controller;
                } else {
                    ListElementController_InventoryGear_NotListView controller = new ListElementController_InventoryGear_NotListView();
                    controller.InitAndFillInSpecificWidget(dac.ComponentRef, null, instance.dataManager, null, dac.Count, false);
                    return controller;
                }
            }).ToList();
            rawInventory = Sort(rawInventory);

            Func<bool, InventoryItemElement_NotListView> mkiie = (bool nonexistant) => {
                var nlv = instance.dataManager.PooledInstantiate( ListElementController_BASE_NotListView.INVENTORY_ELEMENT_PREFAB_NotListView
                                                                                                                              , BattleTechResourceType.UIModulePrefabs, null, null, null)
                                                                                                            .GetComponent<InventoryItemElement_NotListView>();
				if (!nonexistant) {
                    nlv.SetRadioParent(new Traverse(inventoryWidget).Field("inventoryRadioSet").GetValue<HBSRadioSet>());
				    nlv.gameObject.transform.SetParent(new Traverse(inventoryWidget).Field("listParent").GetValue<UnityEngine.Transform>(), false);
				    nlv.gameObject.transform.localScale = UnityEngine.Vector3.one;
                }
                return nlv;
            };

            iieTmp = mkiie(true);

            /* Allocate very few visual elements, as this is extremely slow for both allocation and deallocation.
               It's the difference between a couple of milliseconds and several seconds for many unique items in inventory 
               This is the core of the fix, the rest is just to make it work within HBS's existing code.
               */
            ielCache = Enumerable.Repeat<Func<InventoryItemElement_NotListView>>( () => mkiie(false), itemLimit)
                                 .Select(thunk => thunk())
                                 .ToList();
            var li = new Traverse(inventoryWidget).Field("localInventory").GetValue<List<InventoryItemElement_NotListView>>();
            ielCache.ForEach(iw => li.Add(iw));
            // End



            var lp = new Traverse(inventoryWidget).Field("listParent").GetValue<UnityEngine.Transform>();

            // DummyStart&End are blank rects stored at the beginning and end of the list so that unity knows how big the scrollrect should be
            // "placeholders"
            if (DummyStart == null) DummyStart = new UnityEngine.GameObject().AddComponent<UnityEngine.RectTransform>();
            if (DummyEnd   == null) DummyEnd   = new UnityEngine.GameObject().AddComponent<UnityEngine.RectTransform>();

            DummyStart.SetParent(lp, false);
            DummyEnd.SetParent(lp, false);
            Control.mod.Logger.Log(string.Format("[LimitItems] inventory cached in {0} ms", sw.Elapsed.TotalMilliseconds));

            FilterChanged();
            } catch(Exception e) {
                Control.mod.Logger.Log(string.Format("[LimitItems] exn: {0}", e));
            }
        }

        /* Fast sort, which works off data, rather than visual elements. 
           Since only 7 visual elements are allocated, this is required.
        */
        List<ListElementController_BASE_NotListView> Sort(List<ListElementController_BASE_NotListView> items) {
            var _a = new ListElementController_InventoryGear_NotListView();
            var _b = new ListElementController_InventoryGear_NotListView();
            var _ac = new InventoryItemElement_NotListView();
            var _bc = new InventoryItemElement_NotListView();
            _ac.controller = _a;
            _bc.controller = _b;
            var _cs = new Traverse(inventoryWidget).Field("currentSort").GetValue<Comparison<InventoryItemElement_NotListView>>();
            var tmp = items.ToList();
            tmp.Sort(new Comparison<ListElementController_BASE_NotListView>((l,r) => {
                _a.componentRef = GetRef(l);
                _b.componentRef = GetRef(r);
                return _cs.Invoke(_ac, _bc);
            }));
            return tmp;
        }

        /* Fast filtering code which works off the data, rather than the visual elements.
           Suboptimal due to potential desyncs with normal filter proceedure, but simply required for performance */
        List<ListElementController_BASE_NotListView> Filter(List<ListElementController_BASE_NotListView> items) {
            var iw = new Traverse(inventoryWidget);
            Func<string,bool> f = (n) => iw.Field(n).GetValue<bool>();
            var filter = new InventoryFilter( false //this.filteringAll
                                            , f("filteringWeapons")
                                            , f("filterEnabledWeaponBallistic")
                                            , f("filterEnabledWeaponEnergy")
                                            , f("filterEnabledWeaponMissile")
                                            , f("filterEnabledWeaponSmall")
                                            , f("filteringEquipment")
                                            , f("filterEnabledHeatsink")
                                            , f("filterEnabledJumpjet")
                                            , iw.Field("mechTonnage").GetValue<float>()
                                            , f("filterEnabledUpgrade")
                                            , false );

            ListElementController_BASE tmpctl = new ListElementController_InventoryGear();

            var current = items.Where(item => { 
                tmpctl.weaponDef = null;
                tmpctl.ammoBoxDef = null;
                tmpctl.componentDef = null;
                var def = item.componentDef;
                switch (def.ComponentType) {
                case ComponentType.Weapon:
                    tmpctl.weaponDef = def as WeaponDef;
                    break;
                case ComponentType.AmmunitionBox:
                    tmpctl.ammoBoxDef = def as AmmunitionBoxDef;
                    break;
                case ComponentType.HeatSink:
                case ComponentType.MechPart:
                case ComponentType.JumpJet:
                case ComponentType.Upgrade:
                    tmpctl.componentDef = def;
                    break;
                }
                return filter.Execute(Enumerable.Repeat(tmpctl, 1)).Any();
                }).ToList();
            return current;
        }

        /* Most mods hook the visual element code to filter. This function will do that as quickly as possible
           by re-using a single visual element.
        */
        List<ListElementController_BASE_NotListView> FilterUsingHBSCode(List<ListElementController_BASE_NotListView> items) {
            try {
                var sw = new Stopwatch();
                sw.Start();
            var tmp = inventoryWidget.localInventory;
            var iw = iieTmp;
            inventoryWidget.localInventory = Enumerable.Repeat(iw, 1).ToList();

            // Filter items once using the faster code, then again to handle mods.
            var okItems = Filter(items).Where(lec => {
                var cref = GetRef(lec);
                lec.ItemWidget = iw;
                iw.ComponentRef = cref;
                // Not using SetData here still works, but is much slower
                // TODO: Figure out why.
                iw.SetData(lec, inventoryWidget, lec.quantity, false, null);
                if (!iw.gameObject.activeSelf) { 
                    // Set active is very very slow, only call if absolutely needed
                    // It would be preferable to hook SetActive, but it's an external function.
                    iw.gameObject.SetActive(true); 
                }
                filterGuard = true;
                // Let the main game or any mods filter if needed
                // filter guard is to prevent us from infinitely recursing here, as this is also our triggering patch.
                inventoryWidget.ApplyFiltering(false);
                filterGuard = false;
                lec.ItemWidget = null;
                return iw.gameObject.activeSelf == true;
            }).ToList();
            inventoryWidget.localInventory = tmp;
            Control.mod.Logger.Log(string.Format("Filter took {0} ms and resulted in {1} items", sw.Elapsed.TotalMilliseconds, okItems.Count));

            return okItems;
            } catch (Exception e) {
                Control.mod.Logger.Log(string.Format("[LimitItems] exn filter2: {0}", e));
                return null;
            }
        }

        MechComponentRef GetRef(ListElementController_BASE_NotListView lec) {
            if (lec is ListElementController_InventoryWeapon_NotListView) return (lec as ListElementController_InventoryWeapon_NotListView).componentRef;
            if (lec is ListElementController_InventoryGear_NotListView) return (lec as ListElementController_InventoryGear_NotListView).componentRef;
            Control.mod.Logger.LogError("[LimitItems] lec is not gear or weapon: " + lec.GetId());
            return null;
        }

        /* The user has changed a filter, and we rebuild the item cache. */
        public void FilterChanged() {
            try {
            Control.mod.Logger.Log("[LimitItems] Filter changed");
            index = 0;
            filteredInventory = FilterUsingHBSCode(rawInventory);
            endIndex = filteredInventory.Count - itemLimit;
            Refresh();
            new Traverse(inventoryWidget).Field("scrollbarArea").GetValue<UnityEngine.UI.ScrollRect>().verticalNormalizedPosition = 1.0f;
             } catch (Exception e) {
                Control.mod.Logger.Log(string.Format("[LimitItems] exn filterchanged: {0}", e));
            }
        }

        void Refresh(bool wantClobber = true) {
            #if VVV
            Control.mod.Logger.Log(string.Format("[LimitItems] Refresh: {0} {1} {2} {3}", index, filteredInventory.Count, itemLimit, new Traverse(inventoryWidget).Field("scrollbarArea").GetValue<UnityEngine.UI.ScrollRect>().verticalNormalizedPosition));
            #endif
            if (index > filteredInventory.Count - itemsOnScreen)
                index = filteredInventory.Count - itemsOnScreen;
            if (filteredInventory.Count < itemsOnScreen)
                index = 0;
            if (index < 0)
                index = 0;


            var toShow = filteredInventory.Skip(index).Take(itemLimit).ToList();

            var icc = ielCache.ToList();

            #if VVV
            Control.mod.Logger.Log("[LimitItems] Showing: " + string.Join(",", toShow.Select(lec => lec.componentDef.Description.Name).ToArray()));
            #endif

            toShow.ForEach(lec => {
                var iw = icc[0]; icc.RemoveAt(0);
                var cref = GetRef(lec);
                iw.ClearEverything();
                iw.ComponentRef = cref;
                lec.ItemWidget = iw;
                iw.SetData(lec, inventoryWidget, lec.quantity, false, null);
                lec.SetupLook(iw);
                iw.gameObject.SetActive(true);
            });
            icc.ForEach(unused => unused.gameObject.SetActive(false));


            var tsize = 60.0f;
            
            DummyStart.sizeDelta = new UnityEngine.Vector2(100, tsize * index);
            DummyStart.SetAsFirstSibling();

            var itemsHanging = filteredInventory.Count - (index + itemsOnScreen);

            #if VVV
            Control.mod.Logger.Log("[LimitItems] Items prefixing: " + index);
            Control.mod.Logger.Log("[LimitItems] Items hanging: " + itemsHanging);
            #endif



            DummyEnd.sizeDelta = new UnityEngine.Vector2(100, tsize * itemsHanging);
            DummyEnd.SetAsLastSibling();
            
            
            inventoryWidget.RefreshJumpJetOptions(new Traverse(inventoryWidget).Field("mechTonnage").GetValue<float>());
			new Traverse(instance).Method("RefreshInventorySelectability").GetValue();
            #if VVV
            Control.mod.Logger.Log(string.Format("[LimitItems] RefreshDone {0} {1}", DummyStart.anchoredPosition.y, new Traverse(inventoryWidget).Field("scrollbarArea").GetValue<UnityEngine.UI.ScrollRect>().verticalNormalizedPosition));
            #endif
        }

        static int itemsOnScreen = 7;

        // Maximum # of visual elements to allocate (will be used for slightly off screen elements.)
        static int itemLimit = 7;
        public static UnityEngine.RectTransform DummyStart; 
        public static UnityEngine.RectTransform DummyEnd;
        public static PatchMechlabLimitItems limitItems = null;
        static MethodInfo PopulateInventory = AccessTools.Method(typeof(MechLabPanel), "PopulateInventory");
        static MethodInfo ConfirmRevertMech = AccessTools.Method(typeof(MechLabPanel), "ConfirmRevertMech");
        static MethodInfo ExitMechLab       = AccessTools.Method(typeof(MechLabPanel), "ExitMechLab");

        static bool filterGuard = false;
        public static void Initialize() {
            var onSalvageScreen = AccessTools.Method(typeof(AAR_SalvageScreen), "BeginSalvageScreen");
            Hook.Prefix(onSalvageScreen, Fun.fun(() => {
                // Only for logging purposes.
                Control.mod.Logger.Log("[LimitItems] Open Salvage screen");
            }).Method);
            Hook.Prefix(PopulateInventory, Fun.fun((MechLabPanel __instance) => { 
                if (limitItems != null) Control.mod.Logger.LogError("[LimitItems] PopulateInventory was not properly cleaned");
                Control.mod.Logger.Log("[LimitItems] PopulateInventory patching (Mechlab fix)");
                limitItems = new PatchMechlabLimitItems(__instance);
                return false;
            }).Method);

            Hook.Prefix(ConfirmRevertMech, Fun.fun((MechLabPanel __instance) => { 
                if (limitItems == null) Control.mod.Logger.LogError("[LimitItems] Unhandled ConfirmRevertMech");
                Control.mod.Logger.Log("[LimitItems] Reverting mech");
                limitItems = null;
            }).Method);

            Hook.Prefix(ExitMechLab, Fun.fun((MechLabPanel __instance) => { 
                if (limitItems == null) Control.mod.Logger.LogError("[LimitItems] Unhandled ExitMechLab");
                Control.mod.Logger.Log("[LimitItems] Exiting mechlab");
                limitItems = null;
            }).Method);

            var onLateUpdate = AccessTools.Method(typeof(UnityEngine.UI.ScrollRect), "LateUpdate");
            Hook.Prefix(onLateUpdate, Fun.fun((UnityEngine.UI.ScrollRect __instance) => {
                if (limitItems != null && new Traverse(limitItems.inventoryWidget).Field("scrollbarArea").GetValue<UnityEngine.UI.ScrollRect>() == __instance) {
                    var newIndex = (int)((limitItems.endIndex) * (1.0f - __instance.verticalNormalizedPosition));
                    if (limitItems.filteredInventory.Count < itemsOnScreen) {
                        newIndex = 0;
                    }
                    if (limitItems.index != newIndex) {
                        limitItems.index = newIndex;
                        #if VVV
                        Control.mod.Logger.Log(string.Format("[LimitItems] Refresh with: {0} {1}", newIndex, __instance.verticalNormalizedPosition));
                        #endif
                        limitItems.Refresh(false);
                    }
                }        
            }).Method); 

            var onApplyFiltering = AccessTools.Method(typeof(MechLabInventoryWidget), "ApplyFiltering");
            Hook.Prefix(onApplyFiltering, Fun.fun((MechLabInventoryWidget __instance) => {
                if (limitItems != null && limitItems.inventoryWidget == __instance && !filterGuard) {
                    limitItems.FilterChanged();
                    return false;
                } else {
                    return true;
                }
            }).Method);

            /* FIXME: It's possible for some elements to be in an improper state to this function call. Drop if so.
             */
            Hook.Prefix( AccessTools.Method(typeof(MechLabPanel), "MechCanEquipItem")
                       , Fun.fun((InventoryItemElement_NotListView item) => item.ComponentRef == null ? false : true).Method);

            var onApplySorting = AccessTools.Method(typeof(MechLabInventoryWidget), "ApplySorting");
            Hook.Prefix(onApplySorting, Fun.fun((MechLabInventoryWidget __instance) => {
                if (limitItems != null && limitItems.inventoryWidget == __instance) {
                    // it's a mechlab screen, we do our own sort.
                     return false;
                } else {
                    return true;
                }
            }).Method);            
        }
    }
}