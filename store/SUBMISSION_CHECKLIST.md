# Partner Center submission checklist

| Item | Status | Owner/action |
| --- | --- | --- |
| x64, x86, ARM64 package generation | Prepared by repository | Run `./scripts/release.ps1` |
| Artifact hashes and release manifest | Prepared by repository | Retain the generated `artifacts/store/RELEASE-MANIFEST.md` |
| Package identity and publisher association | Requires Partner Center/account owner | Use exact values from the product association |
| Product name reservation | Requires Partner Center/account owner | Confirm Duplicate File Cleaner Pro availability |
| Store category/properties | Prepared recommendation | Confirm Utilities + tools / File managers in Partner Center |
| English listing copy | Prepared by repository | Review `store/listing/en-US/LISTING.md` |
| Privacy policy | External hosting required | Host `store/privacy-policy.html`, then supply its HTTPS URL |
| Age rating | Requires Partner Center/account owner | Complete the live IARC questionnaire using `store/AGE_RATING_NOTES.md` |
| Pricing, trial, markets, availability | Requires Partner Center/account owner | Make commercial decisions in Partner Center |
| Store screenshots | Requires approved screenshot capture/assets | Use only real release-app captures under `store/assets/screenshots/` |
| Store icon/logo artwork | Requires approved artwork | Replace the current 1x1 placeholder with approved package assets |
| Certification report | Prepared when elevated WACK is available | Run `./scripts/release.ps1 -RunWack` from an elevated interactive session |
| Package upload | Requires Partner Center/account owner | Upload the generated `.msixupload` only after identity/assets are final |
| Submit button | Requires Partner Center/account owner | Review all submission fields; do not automate publication |
