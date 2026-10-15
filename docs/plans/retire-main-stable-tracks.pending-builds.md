# Pending "from Staging Candidate" promotions — builds behind them

Snapshot 2026-10-15. All 25 promotions are `stable → staging`, status Pending; reject each with the comment `release/5 migration`.

## Registered release builds (13) — re-promote via build → staging

Re-running the pipeline re-registers the same version, which auto-creates a `build → staging` promotion. Alternatively, without a rebuild: `POST /api/promotions/from-build {"buildId": "<registry id>", "targetEnv": "staging"}`.

| Product | Service | Version | Build that produced it | InfraPortal registry id |
|---|---|---|---|---|
| mpt | mpt-currency | `5.9.5-g3d6b9347` | [mpt-currency #8877198](https://dev.azure.com/softwareone-pc/e9ec0db2-e7ad-4db1-b724-ad515b61c7c8/_build/results?buildId=8877198) (def 5468, `release/5`, 2026-10-08) | `2102e1e7-9764-4a1a-8a9a-8718eadf9414` |
| mpt | mpt-helpdesk | `5.9.5-gc07f8c7e` | [mpt-helpdesk #8895258](https://dev.azure.com/softwareone-pc/e9ec0db2-e7ad-4db1-b724-ad515b61c7c8/_build/results?buildId=8895258) (def 5535, `release/5`, 2026-10-14) | `516a46d8-a13c-4ca8-a31b-232656ae6f10` |
| mpt | mpt-notifications | `5.9.35-g601ab73f` | [mpt-notifications #8890359](https://dev.azure.com/softwareone-pc/e9ec0db2-e7ad-4db1-b724-ad515b61c7c8/_build/results?buildId=8890359) (def 5358, `release/5`, 2026-10-12) | `bfd4f9c9-c2ec-4294-802a-feb09b0b0273` |
| mpt | mpt-web-currency | `5.9.1-gc1052eb2` | [mpt-web-currency #8857354](https://dev.azure.com/softwareone-pc/e9ec0db2-e7ad-4db1-b724-ad515b61c7c8/_build/results?buildId=8857354) (def 5510, `release/5`, 2026-09-30) | `9314ab3f-ecc2-4bd3-8336-711dd1f3d52f` |
| mpt | swo-extension-aws | `5.9.32-gec01ca41` | [swo-extension-aws-gha #8882153](https://dev.azure.com/softwareone-pc/e9ec0db2-e7ad-4db1-b724-ad515b61c7c8/_build/results?buildId=8882153) (def 5489, `release/5`, 2026-10-09) | `170e1029-f594-4a88-ae2d-e8395b2172d5` |
| mpt | swo-header | `5.9.3-gc5602e57` | [swo-header #8895272](https://dev.azure.com/softwareone-pc/e9ec0db2-e7ad-4db1-b724-ad515b61c7c8/_build/results?buildId=8895272) (def 5517, `release/5`, 2026-10-14) | `e0033889-b4b6-4a69-a201-2f76d996bc84` |
| mpt | swo-web-commerce | `5.9.12-gf1deaa13` | [swo-web-commerce #8882447](https://dev.azure.com/softwareone-pc/e9ec0db2-e7ad-4db1-b724-ad515b61c7c8/_build/results?buildId=8882447) (def 5111, `release/5`, 2026-10-09) | `ea00ec49-e83c-4729-9cfd-a37bb2458e9d` |
| mpt | swo-web-errorpages | `5.9.2-gde3a55ed` | [swo-web-errorpages #8895211](https://dev.azure.com/softwareone-pc/e9ec0db2-e7ad-4db1-b724-ad515b61c7c8/_build/results?buildId=8895211) (def 5532, `release/5`, 2026-10-14) | `6752ca6c-1efe-4767-bb33-792c8aa35cf3` |
| mpt | swo-web-header | `5.9.2-gbce97ab5` | [swo-web-header #8858443](https://dev.azure.com/softwareone-pc/e9ec0db2-e7ad-4db1-b724-ad515b61c7c8/_build/results?buildId=8858443) (def 5113, `release/5`, 2026-09-30) | `002f8655-b80d-48f8-b007-1d23393683d2` |
| mpt | swo-web-purchasewizard | `5.9.7-ge3c49c2c` | [swo-web-purchasewizard #8882382](https://dev.azure.com/softwareone-pc/e9ec0db2-e7ad-4db1-b724-ad515b61c7c8/_build/results?buildId=8882382) (def 5205, `release/5`, 2026-10-09) | `a7494f36-b388-4f21-8f03-3b603cfbcbd4` |
| mpt-extensions | mpt-extension-adobe | `5.1.211-gc872b62b` | [mpt-extension-adobe #8896146](https://dev.azure.com/softwareone-pc/e9ec0db2-e7ad-4db1-b724-ad515b61c7c8/_build/results?buildId=8896146) (def 5655, `release/5`, 2026-10-14) | `e2bf959b-9e08-4758-81a7-825501ae4608` |
| mpt-extensions | mpt-extension-operations | `5.9.3-g0024a539` | [mpt-extension-operations #8895462](https://dev.azure.com/softwareone-pc/e9ec0db2-e7ad-4db1-b724-ad515b61c7c8/_build/results?buildId=8895462) (def 5628, `release/5`, 2026-10-14) | `5cf09f34-b8a8-45a7-b1a1-0b788a725f81` |
| mpt-extensions | swo-extension-mscsp | `5.9.117-gd8efe772` | [swo-extension-mscsp #8895546](https://dev.azure.com/softwareone-pc/e9ec0db2-e7ad-4db1-b724-ad515b61c7c8/_build/results?buildId=8895546) (def 5165, `release/5`, 2026-10-14) | `1733cdb2-b355-47cf-a087-b3ceeea4f5c1` |

## No registered build (12)

| Product | Service | Pending version | Note |
|---|---|---|---|
| mpt | mpt-cli-adobe-plugin | `5.9.1-gd74cf44a` | not deployable (library/template/static page); nothing to rerun |
| mpt | mpt-library-framework | `5.9.10-g6cf0aff9` | not deployable (library/template/static page); nothing to rerun |
| mpt | swo-design-system | `5.9.20-g88e00b6f` | not deployable (library/template/static page); nothing to rerun |
| mpt | swo-dynamic-translations | `5.9.0` | not deployable (library/template/static page); nothing to rerun |
| mpt | swo-lib-dotnet-appinsights | `5.9.0` | not deployable (library/template/static page); nothing to rerun |
| mpt | swo-lib-dotnet-servicebus | `5.9.0` | not deployable (library/template/static page); nothing to rerun |
| mpt | swo-lib-playwright-e2e | `5.9.0` | not deployable (library/template/static page); nothing to rerun |
| mpt | swo-new-react-template | `5.9.0` | not deployable (library/template/static page); nothing to rerun |
| mpt | swo-web-healthcheck | `5.9.0` | not deployable (library/template/static page); nothing to rerun |
| mpt | swo-web-libraries | `5.9.16-g275b917c` | not deployable (library/template/static page); nothing to rerun |
| mpt | swo-web-maintenence | `5.9.0` | not deployable (library/template/static page); nothing to rerun |
| mpt | swo-web-oneheader-container | `5.9.0` | `5.9.0` is a manifest placeholder, not a build. Latest registered release build: `5.9.3-g4b14d5ad` [swo-web-oneheader-container #8895460](https://dev.azure.com/softwareone-pc/e9ec0db2-e7ad-4db1-b724-ad515b61c7c8/_build/results?buildId=8895460) (registry id `683fd382-0069-4a65-8b4c-5f9b5a6c4b82`) |
