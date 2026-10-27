using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HellmartCoop
{
    // Working the register from a client. The sale itself always happens on the host (its customer,
    // its tape, its CashRegister, its cash); the client gets a working copy of the counter and its
    // actions are replayed on the host's register with the game's own methods.
    //
    // Host → client (world channel 8, 5 Hz): tape items (stable id, product key, position, yaw,
    //   scanned) and register state (registered, open, final price, customer's cash).
    // Client: each tape item becomes a real local product registered with the client's ScannerSystem,
    //   so the client's normal scanning and bagging work on it. The register screen shows the host's
    //   numbers. The customer's cash on the mirrored customer is clickable again.
    // Client → host (action channel 9): seat on/off, scan id, bag id, take the customer's cash,
    //   give change (value), take change back, complete purchase.
    public static class CheckoutSync
    {
        public const byte Channel = 8;
        public const byte ActionChannel = 9;
        private const float Interval = 0.2f;

        private const byte ActSeat = 0, ActScan = 1, ActBag = 2, ActCash = 3, ActChange = 4, ActBack = 5, ActComplete = 6,
            ActCigarette = 7, ActCigaretteBack = 8;

        private static readonly MethodInfo ScanItem = AccessTools.Method(typeof(ScannerSystem), "ScanItem");
        private static readonly MethodInfo SetHaveProducts = AccessTools.PropertySetter(typeof(StoreCheckout), nameof(StoreCheckout.HaveProducts));

        private static bool IsClient => WorldClock.IsClient;
        private static bool IsHost => Plugin.Instance.Net != null && Plugin.Instance.Net.IsHost;

        // While true, our own calls into the game must not be reported back as client actions.
        private static bool applying;

        // Host: ids for the ScanObjects on its tape. Client: id → its local copy.
        private static readonly Dictionary<ScanObject, ushort> hostIds = new Dictionary<ScanObject, ushort>();
        private static ushort nextId = 1;
        private static readonly Dictionary<ushort, ScanObject> clientItems = new Dictionary<ushort, ScanObject>();
        private static readonly Dictionary<ScanObject, ushort> clientIds = new Dictionary<ScanObject, ushort>();
        private static float nextSend;
        // Host: a client is sitting at the register.
        private static bool remoteCashier;
        public static bool RemoteCashier => remoteCashier;
        private static bool hostWasRegistered;
        // Host: a client clicked the cash a moment before the customer held it out.
        private static float pendingCashUntil = -1f;
        private static byte pendingCashClient;
        // Client: the host's customer at the counter is holding out cash.
        public static bool HostCashOffered { get; private set; }
        // Client: the host's customer at the counter asked for cigarettes.
        public static bool HostWantsCigarette { get; private set; }

        private static CustomerCash OfferedCash()
        {
            var customer = StoreCheckout.Instance != null ? StoreCheckout.Instance.CurrentCustomer : null;
            // Every body model in a customer prefab has its own CustomerCash; only the one in the
            // active model, held out (active) right now, counts.
            return customer != null ? customer.GetComponentInChildren<CustomerCash>(false) : null;
        }

        public static void Register(Net net)
        {
            net.OnWorld(Channel, Apply);
            net.OnAction(ActionChannel, HostApplyAction);
        }

        public static void Clear()
        {
            hostIds.Clear();
            clientItems.Clear();
            clientIds.Clear();
            remoteCashier = false;
        }

        // ---------------- host ----------------

        [HarmonyPatch(typeof(ScannerSystem), nameof(ScannerSystem.AddToTape))]
        private static class TrackTape
        {
            private static void Postfix(ScanObject scanObject)
            {
                if (IsHost && scanObject != null && !hostIds.ContainsKey(scanObject))
                    hostIds[scanObject] = nextId++;
            }
        }

        // A client at the register counts as someone at the register for the host's customers
        // (cigarette questions, special customers talking, "pay" condition).
        [HarmonyPatch(typeof(StoreCheckout), nameof(StoreCheckout.IsActive), MethodType.Getter)]
        private static class RemoteCashierIsActive
        {
            private static void Postfix(ref bool __result)
            {
                if (remoteCashier && IsHost)
                    __result = true;
            }
        }

        public static void HostUpdate(Net net)
        {
            if (!net.IsHost || ScannerSystem.Instance == null || CashRegister.Instance == null || Time.unscaledTime < nextSend)
                return;
            // Test (HMCOOP_CIGTEST=host): every customer at the counter wants cigarettes.
            var current = StoreCheckout.Instance != null ? StoreCheckout.Instance.CurrentCustomer : null;
            if (current is DefaultCustomer && System.Environment.GetEnvironmentVariable("HMCOOP_CIGTEST") == "host")
            {
                var t = Traverse.Create(current);
                if (!t.Field<bool>("askedCigarette").Value && !t.Field<bool>("wannaCigarette").Value)
                    t.Field("wannaCigarette").SetValue(true);
            }
            nextSend = Time.unscaledTime + Interval;
            var tape = new List<ScanObject>();
            foreach (var so in ScannerSystem.Instance.TapeProducts)
            {
                if (so != null && hostIds.ContainsKey(so))
                    tape.Add(so);
            }
            var register = CashRegister.Instance;
            var cr = Traverse.Create(register);
            var offered = OfferedCash();
            if (pendingCashUntil > 0f)
            {
                if (offered != null)
                {
                    pendingCashUntil = -1f;
                    TakeCash(offered, pendingCashClient);
                    offered = null;
                }
                else if (Time.unscaledTime > pendingCashUntil)
                {
                    pendingCashUntil = -1f;
                    Plugin.Log.LogInfo("Checkout: the customer never held out cash for the client's click");
                }
            }
            net.BroadcastWorld(Channel, w =>
            {
                w.Write(offered != null);
                w.Write(register.Registered);
                w.Write(register.IsRegisterOpen);
                w.Write(register.FinalPrice);
                w.Write(cr.Field<float>("cash").Value);
                w.Write(StoreCheckout.Instance != null && StoreCheckout.Instance.HaveProducts);
                w.Write(StoreCheckout.Instance != null && StoreCheckout.Instance.CustomerWantCigarette());
                w.Write((byte)Mathf.Min(tape.Count, 40));
                for (int i = 0; i < tape.Count && i < 40; i++)
                {
                    var so = tape[i];
                    var product = so.GetComponent<Product>() ?? so.GetComponentInParent<Product>();
                    w.Write(hostIds[so]);
                    // Cigarettes have no saveKey; fall back to the ProductData asset name.
                    var config = product != null ? product.ProductConfig : null;
                    w.Write(config == null ? "" : !string.IsNullOrEmpty(config.saveKey) ? config.saveKey : "name:" + config.name);
                    var t = product != null ? product.transform : so.transform;
                    w.Write(t.position.x);
                    w.Write(t.position.y);
                    w.Write(t.position.z);
                    w.Write(t.eulerAngles.y);
                    w.Write(so.IsScanned);
                }
            });
            if (hostWasRegistered && !register.Registered)
                Plugin.Log.LogInfo("Checkout: register reset (sale finished)");
            hostWasRegistered = register.Registered;
        }

        // The host's cigarette of that product in the pack at that scene path.
        private static Cigarette HostCigarette(uint packId, string key)
        {
            foreach (var pack in Object.FindObjectsByType<Pack>(FindObjectsSortMode.None))
            {
                if (ShelfSync.PathId(pack.transform) != packId)
                    continue;
                foreach (var cig in pack.GetComponentsInChildren<Cigarette>())
                {
                    var p = cig.GetComponent<Product>();
                    if (p != null && p.ProductConfig != null && p.ProductConfig.saveKey == key)
                        return cig;
                }
            }
            return null;
        }

        private static void TakeCash(CustomerCash cash, byte clientId)
        {
            cash.GiveCash();
            Plugin.Log.LogInfo($"Checkout: client {clientId} took the customer's cash, price {CashRegister.Instance.FinalPrice:F2}");
        }

        private static ScanObject HostItem(ushort id)
        {
            foreach (var pair in hostIds)
            {
                if (pair.Value == id && pair.Key != null)
                    return pair.Key;
            }
            return null;
        }

        private static void HostApplyAction(byte clientId, BinaryReader r)
        {
            byte act = r.ReadByte();
            var scanner = ScannerSystem.Instance;
            var checkout = StoreCheckout.Instance;
            var register = CashRegister.Instance;
            if (scanner == null || checkout == null || register == null)
                return;
            switch (act)
            {
                case ActSeat:
                    remoteCashier = r.ReadBoolean();
                    Plugin.Log.LogInfo($"Checkout: client {clientId} {(remoteCashier ? "sat down at" : "left")} the register");
                    break;
                case ActScan:
                {
                    var so = HostItem(r.ReadUInt16());
                    if (so != null && !so.IsScanned)
                    {
                        ScanItem.Invoke(scanner, new object[] { so });
                        Plugin.Log.LogInfo($"Checkout: client {clientId} scanned {so.GetComponentInParent<Product>()?.ProductConfig?.saveKey}");
                    }
                    break;
                }
                case ActBag:
                {
                    var so = HostItem(r.ReadUInt16());
                    if (so != null)
                    {
                        hostIds.Remove(so);
                        scanner.ForceBag(so);
                        Plugin.Log.LogInfo($"Checkout: client {clientId} bagged an item, {scanner.TapeProducts.Count} left on the tape");
                    }
                    break;
                }
                case ActCash:
                {
                    var cash = OfferedCash();
                    if (cash != null)
                    {
                        TakeCash(cash, clientId);
                    }
                    else
                    {
                        pendingCashUntil = Time.unscaledTime + 10f;
                        pendingCashClient = clientId;
                        var customer = checkout.CurrentCustomer;
                        bool cigarette = customer is DefaultCustomer && Traverse.Create(customer).Field<bool>("wannaCigarette").Value;
                        Plugin.Log.LogInfo($"Checkout: client {clientId} reached for cash before the customer offered it (customer {(customer != null ? customer.name : "none")}{(cigarette ? ", wants cigarettes" : "")}), waiting");
                    }
                    break;
                }
                case ActChange:
                {
                    float value = r.ReadSingle();
                    foreach (var source in Object.FindObjectsByType<ChangeCash>(FindObjectsSortMode.None))
                    {
                        if (Mathf.Abs(Traverse.Create(source).Field<float>("value").Value - value) < 0.001f)
                        {
                            source.TakeChange();
                            break;
                        }
                    }
                    break;
                }
                case ActBack:
                    register.BackChange();
                    break;
                case ActCigarette:
                {
                    uint packId = r.ReadUInt32();
                    string key = r.ReadString();
                    var cig = HostCigarette(packId, key);
                    if (cig != null && checkout.CustomerWantCigarette())
                    {
                        cig.Take();
                        Plugin.Log.LogInfo($"Checkout: client {clientId} handed over cigarettes '{key}'");
                    }
                    else
                    {
                        Plugin.Log.LogInfo($"Checkout: client {clientId} cigarettes '{key}' not possible (found {cig != null}, wanted {checkout.CustomerWantCigarette()})");
                    }
                    break;
                }
                case ActCigaretteBack:
                {
                    var so = HostItem(r.ReadUInt16());
                    var cig = so != null ? so.GetComponent<Cigarette>() : null;
                    if (cig != null)
                    {
                        hostIds.Remove(so);
                        cig.GetBack();
                        Plugin.Log.LogInfo($"Checkout: client {clientId} took the cigarettes back");
                    }
                    break;
                }
                case ActComplete:
                {
                    bool could = checkout.CanPurchase();
                    float before = PlayerController.Instance != null ? PlayerController.Instance.Cash : 0f;
                    checkout.CompletePurchase();
                    float after = PlayerController.Instance != null ? PlayerController.Instance.Cash : 0f;
                    Plugin.Log.LogInfo($"Checkout: client {clientId} completed the purchase (allowed {could}), store cash {before:F2} -> {after:F2}");
                    break;
                }
            }
        }

        // ---------------- client ----------------

        private static void SendAction(byte act, System.Action<BinaryWriter> body = null)
        {
            var net = Plugin.Instance.Net;
            if (applying || net == null || net.IsHost)
                return;
            net.SendAction(ActionChannel, w =>
            {
                w.Write(act);
                body?.Invoke(w);
            });
        }

        private static void Apply(BinaryReader r)
        {
            HostCashOffered = r.ReadBoolean();
            bool registered = r.ReadBoolean();
            bool open = r.ReadBoolean();
            float finalPrice = r.ReadSingle();
            float cash = r.ReadSingle();
            bool haveProducts = r.ReadBoolean();
            HostWantsCigarette = r.ReadBoolean();
            int count = r.ReadByte();
            var seen = new HashSet<ushort>();
            var scanner = ScannerSystem.Instance;
            var checkout = StoreCheckout.Instance;
            var register = CashRegister.Instance;
            applying = true;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    ushort id = r.ReadUInt16();
                    string key = r.ReadString();
                    var position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    float yaw = r.ReadSingle();
                    bool scanned = r.ReadBoolean();
                    seen.Add(id);
                    if (scanner == null || checkout == null)
                        continue;
                    if (!clientItems.TryGetValue(id, out var so) || so == null)
                    {
                        so = CreateTapeItem(key, position, yaw);
                        if (so == null)
                            continue;
                        clientItems[id] = so;
                        clientIds[so] = id;
                        SetHaveProducts.Invoke(checkout, new object[] { true });
                    }
                    if (scanned && !so.IsScanned)
                        ScanItem.Invoke(scanner, new object[] { so });
                }

                // Gone on the host (bagged there, or the sale ended): remove our copy.
                var gone = new List<ushort>();
                foreach (var id in clientItems.Keys)
                {
                    if (!seen.Contains(id))
                        gone.Add(id);
                }
                foreach (var id in gone)
                {
                    var so = clientItems[id];
                    clientItems.Remove(id);
                    if (so != null)
                    {
                        clientIds.Remove(so);
                        if (scanner != null && scanner.TapeProducts.Contains(so))
                            scanner.ForceRemoveProduct(so);
                        else
                            Object.Destroy(so.gameObject);
                    }
                }

                if (register != null)
                    ApplyRegister(register, registered, open, finalPrice, cash);
            }
            finally
            {
                applying = false;
            }
        }

        private static void ApplyRegister(CashRegister register, bool registered, bool open, float finalPrice, float cash)
        {
            var cr = Traverse.Create(register);
            bool wasRegistered = register.Registered;
            if (wasRegistered && !registered)
            {
                // Sale done on the host: clear our change and screen like ResetAfterPurchase does,
                // without ResetCash's quota bookkeeping (that's the host's).
                while (register.ChangeList.Count > 0)
                    register.DeleteChange();
                if (CheckoutScreen.Instance != null)
                    CheckoutScreen.Instance.Reset();
                Plugin.Log.LogInfo("Checkout: the host finished the sale");
            }
            cr.Field<bool>("registered").Value = registered;
            cr.Field<float>("finalPrice").Value = finalPrice;
            cr.Field<float>("cash").Value = cash;
            if (open && !register.IsRegisterOpen)
                register.OpenRegister();
            else if (!open && register.IsRegisterOpen)
                register.CloseRegister();
            if (!wasRegistered && registered)
                Plugin.Log.LogInfo($"Checkout: customer paid {cash:F2} for {finalPrice:F2}");
            register.UpdateRegisterScreen();
        }

        private static ScanObject CreateTapeItem(string key, Vector3 position, float yaw)
        {
            var config = Products.Find(key);
            if (config == null)
            {
                Plugin.Log.LogWarning($"Checkout: unknown product '{key}' on the host's tape");
                return null;
            }
            var product = config.CreateProduct();
            product.SetForm(Product.ModelForm.Default);
            product.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            var so = product.GetComponentInChildren<ScanObject>();
            if (so == null)
            {
                Object.Destroy(product.gameObject);
                return null;
            }
            ScannerSystem.Instance.AddToTape(so);
            return so;
        }

        public static int ClientTapeCount => clientItems.Count;

        [HarmonyPatch(typeof(StoreCheckout), "Activate")]
        private static class ClientSits
        {
            private static void Postfix() => SendAction(ActSeat, w => w.Write(true));
        }

        [HarmonyPatch(typeof(StoreCheckout), "Deactivate")]
        private static class ClientStands
        {
            private static void Postfix() => SendAction(ActSeat, w => w.Write(false));
        }

        [HarmonyPatch(typeof(ScannerSystem), "ScanItem")]
        private static class ClientScans
        {
            private static void Postfix(ScanObject scanObject)
            {
                if (IsClient && clientIds.TryGetValue(scanObject, out ushort id))
                    SendAction(ActScan, w => w.Write(id));
            }
        }

        [HarmonyPatch(typeof(ScannerSystem), nameof(ScannerSystem.ForceBag))]
        private static class ClientBags
        {
            private static void Prefix(ScanObject scanObject)
            {
                if (!IsClient || applying || !clientIds.TryGetValue(scanObject, out ushort id))
                    return;
                clientIds.Remove(scanObject);
                clientItems.Remove(id);
                SendAction(ActBag, w => w.Write(id));
            }
        }

        // The cash a mirrored customer holds out: pay on the host instead of locally.
        [HarmonyPatch(typeof(CustomerCash), nameof(CustomerCash.GiveCash))]
        private static class ClientTakesCash
        {
            private static bool Prefix(CustomerCash __instance)
            {
                if (!IsClient)
                    return true;
                SendAction(ActCash);
                __instance.gameObject.SetActive(false);
                return false;
            }
        }

        [HarmonyPatch(typeof(CashRegister), nameof(CashRegister.AddChange))]
        private static class ClientGivesChange
        {
            private static void Postfix(Change change)
            {
                if (IsClient && change != null)
                    SendAction(ActChange, w => w.Write(change.Value));
            }
        }

        [HarmonyPatch(typeof(CashRegister), nameof(CashRegister.BackChange))]
        private static class ClientTakesChangeBack
        {
            private static void Prefix(CashRegister __instance)
            {
                if (IsClient && __instance.ChangeList.Count > 0)
                    SendAction(ActBack);
            }
        }

        // Client: "does the customer want cigarettes" is the host's customer's wish.
        [HarmonyPatch(typeof(StoreCheckout), nameof(StoreCheckout.CustomerWantCigarette))]
        private static class ClientCigaretteWish
        {
            private static bool Prefix(ref bool __result)
            {
                if (!IsClient)
                    return true;
                __result = HostWantsCigarette;
                return false;
            }
        }

        // Client: hand cigarettes over on the host; here they only leave the rack (the host's tape
        // brings them onto ours).
        [HarmonyPatch(typeof(Cigarette), nameof(Cigarette.Take))]
        private static class ClientTakesCigarette
        {
            private static bool Prefix(Cigarette __instance)
            {
                if (!IsClient)
                    return true;
                var pack = __instance.GetComponentInParent<Pack>();
                var product = __instance.GetComponent<Product>();
                if (pack == null || product == null || product.ProductConfig == null)
                    return false;
                uint packId = ShelfSync.PathId(pack.transform);
                string key = product.ProductConfig.saveKey;
                SendAction(ActCigarette, w =>
                {
                    w.Write(packId);
                    w.Write(key);
                });
                HostWantsCigarette = false;
                pack.ReduceProduct(product);
                Object.Destroy(product.gameObject);
                Plugin.Log.LogInfo($"Checkout: handing over cigarettes '{key}'");
                return false;
            }
        }

        [HarmonyPatch(typeof(Cigarette), nameof(Cigarette.GetBack))]
        private static class ClientCigaretteBack
        {
            private static void Prefix(Cigarette __instance)
            {
                var so = __instance.GetComponent<ScanObject>();
                if (!IsClient || so == null || !clientIds.TryGetValue(so, out ushort id))
                    return;
                clientIds.Remove(so);
                clientItems.Remove(id);
                SendAction(ActCigaretteBack, w => w.Write(id));
            }
        }

        [HarmonyPatch(typeof(StoreCheckout), nameof(StoreCheckout.CompletePurchase))]
        private static class ClientCompletes
        {
            private static bool Prefix()
            {
                if (!IsClient)
                    return true;
                SendAction(ActComplete);
                return false;
            }
        }
    }
}
