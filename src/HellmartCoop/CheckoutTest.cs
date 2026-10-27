using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HellmartCoop
{
    // Test bot (HMCOOP_CHECKOUTTEST=1, client): serves one customer at the register from the client
    // with the same game calls a player's clicks end in: sit down, scan every item, bag it, take the
    // customer's cash, give change from the drawer, complete the purchase. Everything it does goes
    // through CheckoutSync's hooks to the host. It keeps serving customers until one needed change
    // (at most 4 sales), so the change path is covered too.
    public static class CheckoutTest
    {
        private static readonly MethodInfo ScanItem = AccessTools.Method(typeof(ScannerSystem), "ScanItem");
        private static int step;
        private static int sales;
        private static bool changeTested;
        private static float next;
        private static float stepStarted;

        public static void ClientUpdate()
        {
            if (step >= 7 || System.Environment.GetEnvironmentVariable("HMCOOP_CHECKOUTTEST") != "1")
                return;
            var scanner = ScannerSystem.Instance;
            var checkout = StoreCheckout.Instance;
            var register = CashRegister.Instance;
            if (scanner == null || checkout == null || register == null || Time.unscaledTime < next)
                return;
            next = Time.unscaledTime + 0.5f;

            switch (step)
            {
                case 0:
                    if (CheckoutSync.ClientTapeCount == 0)
                        return;
                    Plugin.Log.LogInfo($"Checkout test: {CheckoutSync.ClientTapeCount} items on the tape, sitting down");
                    checkout.Interact();
                    Advance(1.5f);
                    break;
                case 1:
                    int scanned = 0;
                    foreach (var so in scanner.TapeProducts.ToList())
                    {
                        if (so != null && !so.IsScanned)
                        {
                            ScanItem.Invoke(scanner, new object[] { so });
                            scanned++;
                        }
                    }
                    Plugin.Log.LogInfo($"Checkout test: scanned {scanned}");
                    Advance(1.5f);
                    break;
                case 2:
                    // Items the host added after we scanned: scan those first.
                    if (scanner.TapeProducts.Any(so => so != null && !so.IsScanned))
                    {
                        step = 1;
                        return;
                    }
                    int bagged = 0;
                    foreach (var so in scanner.TapeProducts.ToList())
                    {
                        if (so != null)
                        {
                            scanner.ForceBag(so);
                            bagged++;
                        }
                    }
                    Plugin.Log.LogInfo($"Checkout test: bagged {bagged}");
                    Advance(0.5f);
                    break;
                case 3:
                    // The host says when its customer holds out cash; then click it on our copy.
                    var cash = CheckoutSync.HostCashOffered
                        ? Object.FindObjectsByType<CustomerCash>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                            .FirstOrDefault(c => c.transform.root.name.StartsWith("CoopCustomer_"))
                        : null;
                    if (cash == null)
                    {
                        if (Time.unscaledTime - stepStarted > 60f)
                            Fail(CheckoutSync.HostCashOffered ? "the customer copy shows no cash" : "the customer never held out cash");
                        return;
                    }
                    Plugin.Log.LogInfo("Checkout test: taking the customer's cash");
                    cash.GiveCash();
                    Advance(0.5f);
                    break;
                case 4:
                    if (!register.Registered)
                    {
                        if (Time.unscaledTime - stepStarted > 20f)
                            Fail("the host never registered the payment");
                        return;
                    }
                    float cashGiven = Traverse.Create(register).Field<float>("cash").Value;
                    float remaining = cashGiven - register.FinalPrice;
                    Plugin.Log.LogInfo($"Checkout test: customer paid {cashGiven:F2} for {register.FinalPrice:F2}, change due {remaining:F2}");
                    var sources = Object.FindObjectsByType<ChangeCash>(FindObjectsSortMode.None)
                        .Select(c => (source: c, value: Traverse.Create(c).Field<float>("value").Value))
                        .OrderByDescending(c => c.value)
                        .ToList();
                    var given = new List<float>();
                    for (int i = 0; i < 30 && remaining > 0.004f && sources.Count > 0; i++)
                    {
                        var pick = sources.FirstOrDefault(c => c.value <= remaining + 0.001f);
                        if (pick.source == null)
                            pick = sources.Last();
                        pick.source.TakeChange();
                        given.Add(pick.value);
                        remaining -= pick.value;
                    }
                    Plugin.Log.LogInfo($"Checkout test: gave change [{string.Join(", ", given.Select(v => v.ToString("F2")))}]");
                    if (given.Count > 0)
                        changeTested = true;
                    Advance(2f);
                    break;
                case 5:
                    Plugin.Log.LogInfo("Checkout test: completing the purchase");
                    checkout.CompletePurchase();
                    Advance(0.5f);
                    break;
                case 6:
                    if (!register.Registered)
                    {
                        sales++;
                        if (!changeTested && sales < 4)
                        {
                            Plugin.Log.LogInfo($"Checkout test: sale {sales} done without change, serving the next customer");
                            step = 0;
                            return;
                        }
                        Plugin.Log.LogInfo($"Checkout test: PASSED, the host finished {sales} sale(s), change given: {changeTested}");
                        step = 7;
                    }
                    else if (Time.unscaledTime - stepStarted > 15f)
                    {
                        Fail("the host did not finish the sale");
                    }
                    break;
            }
        }

        private static void Advance(float wait)
        {
            step++;
            stepStarted = Time.unscaledTime;
            next = Time.unscaledTime + wait;
        }

        private static void Fail(string why)
        {
            Plugin.Log.LogWarning($"Checkout test: FAILED at step {step}: {why}");
            step = 7;
        }
    }
}
