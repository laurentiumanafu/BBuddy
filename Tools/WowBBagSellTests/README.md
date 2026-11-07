# Bag selling priority checks

`python Tools/WowBBagSellTests/test_vendor_priority.py` checks source wiring only. `python Tools/WowBBagSellTests/run.py` requires a .NET 10 SDK and exercises the extracted production `VendorDue` against synthetic observations, combat, retries, identity changes and settings. The inventory reader tests cover the production asynchronous bag monitor. The candidate zero-tag subtotal, the recorded plan, the merchant window and any sale remain unverified in a live client; an automatic trip is not proof that items sold.
