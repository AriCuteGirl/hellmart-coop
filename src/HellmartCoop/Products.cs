using System.Collections.Generic;
using UnityEngine;

namespace HellmartCoop
{
    // ProductManager.FindProduct only knows the store's normal stock; anomaly products (e.g.
    // anomaly_product_freezer) are ProductData too, so fall back to every loaded one.
    public static class Products
    {
        private static Dictionary<string, ProductData> all;

        public static ProductData Find(string key)
        {
            var product = ProductManager.FindProduct(key);
            if (product != null)
                return product;
            if (all == null || !all.ContainsKey(key))
            {
                all = new Dictionary<string, ProductData>();
                foreach (var p in Resources.FindObjectsOfTypeAll<ProductData>())
                {
                    if (!string.IsNullOrEmpty(p.saveKey) && !all.ContainsKey(p.saveKey))
                        all[p.saveKey] = p;
                }
            }
            all.TryGetValue(key, out product);
            return product;
        }
    }
}
