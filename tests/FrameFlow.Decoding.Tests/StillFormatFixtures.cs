namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Single-image files FFmpeg's image2 demuxer cannot name a decoder for from the extension (#575),
/// written with Pillow: a static GIF, a three-frame GIF, a two-size ICO (16x16 and 32x32) and an AVIF. The rest are 32x32.
/// </summary>
internal static class StillFormatFixtures
{
    public static byte[] Gif => Convert.FromBase64String(GifBase64);

    public static byte[] AnimatedGif => Convert.FromBase64String(AnimatedGifBase64);

    public static byte[] Ico => Convert.FromBase64String(IcoBase64);

    public static byte[] Avif => Convert.FromBase64String(AvifBase64);

    private const string GifBase64 =
            "R0lGODdhIAAgAIcAAO/zgO/ngNvvgPPbgOfbgNvbgMfzgLfzgMPngMfbgLfbgO/LgO+/gNvHgOe3gMfLgLfLgMO/gMO3gKfz" +
            "gKfngJPvgIPzgHfzgH/ngKvbgJ/bgJPbgIPbgHfbgKfLgKe/gJPHgJ+3gIPLgHfLgH+/gH+3gGfzgGfngFPvgEPzgDfzgD/n" +
            "gGvbgF/bgFPbgD/bgCfzgBfzgCPngAf3gAfvgAfngCfbgBfbgAfbgGfLgGe/gFPLgFO/gF+3gEPLgDfLgD+/gD+3gCfLgBfL" +
            "gCO/gCO3gAfLgAe/gAe3gO+rgO+fgNungPOTgOeTgNuTgMergLergMOfgMeTgLeTgO+DgO93gNt/gMeDgLeDgMN3gKergKef" +
            "gJOrgJOfgH+rgH+fgKuTgJ+TgJOTgH+TgKeDgKd3gJODgJN3gH+DgH93gGergGefgFOngEOrgDergD+fgGuTgF+TgFOTgD+T" +
            "gGeDgGd3gFODgFN3gD+DgD93gCergBergCOfgAergAefgCeTgBeTgAeXgAePgCeDgBeDgCN3gAeHgAd/gAd3gO9rgO9fgNtn" +
            "gPNTgOdTgNtTgMdrgLdrgMNfgMdTgLdTgO9DgO83gNs/gMdDgLdDgMM3gKdrgKdfgJNngINrgHdrgH9fgKtTgJ9TgJNTgH9T" +
            "gKdDgKc3gJM/gINDgHdDgH83gO8rgO8fgNsrgNsfgOcXgMcrgLcrgMMfgMMXgO8LgO8AgNsHgMcLgLcLgMMAgKcrgKcfgJMn" +
            "gJ8XgIMrgHcrgH8fgH8XgKcLgKcAgJMHgIMLgHcLgH8AgGdrgGdfgFNrgFNfgD9rgD9fgGtTgF9TgFNTgD9TgGdDgGc3gFND" +
            "gFM3gD9DgD83gCdrgBdrgCNfgAdvgAdngAdfgCdTgBdTgAdTgCdDgBdDgCM3gAdHgAc/gAc3gGcrgGcfgFMngF8XgEMrgDcr" +
            "gD8fgD8XgGcLgGcAgFMHgEMLgDcLgD8AgCcrgBcrgCMfgCMXgAcrgAcfgAcXgCcLgBcLgCMAgAcLgAcAgCwAAAAAIAAgAEAI" +
            "/wD/CeTXr+C8g/DgvVtorOGwYcIi3sJFsVYtWhjJaQwHrmO1j9OmRRuJ6tQpU6ZIqcR0qaUlS5RiHppJaJBNPDjt2KHDE41P" +
            "M2bICMVypagVK1SSHllKpCkRIFB58NBBlYRVECA+aI3ANUKDBgzC5htrr6w9dmjRoTvH9pfbXbt0yY1FN1arVqvyctubrW82" +
            "ZoCVKUNG+JNhTpw2KY7EONKiRYoi+5nMpzKfN5jZsFnD+YvnLl22iI5COsqSJUpS41h9w4brF7BduGjBgkUHDhw2bNCQIYOC" +
            "BMALFCAwYIC/4/z2KZcXL17CdO7cFSNG7CGvYMFu2dpu0dWsWfjC1/+jR36dOnVqzan31asX3FzwYb2az4qVqvvf8nvrxh+a" +
            "/2fPONNMM6MUKIoooYACyiSSNOiII40wwog2FGJzzYXLZJhMMsd06EknnSCmyYiQPGLiY4mkKMiKgPzh4hwwyiFHHHDAMcaN" +
            "YogRBhhgTCHFj0440QQTTPRh5B56JOlGG21o1oMaanghJRdchKCFFlA8oeVpDiSRhBFgDiHEmD/44MMOO+Sg5ggiiICVB3BC" +
            "8MCcXy1gJw14xgDDniqkkAIKKJgg6AUWWFBBBRMkeoABjAogAACQHucPQcs1l1B00lH3EHbZ4cJdLd/Nos+o95R6TzuopqNq" +
            "OucA4yovsPL/oosstMrSiiu4rhIePuOVd15667X3XnzzvVLffaqUo6w4zIpjzbPUUCPNtKlUi1Ip2GaibSYvVeLtOOBy5BFI" +
            "IpFkEkoqkcKSSzDFlN83+/X3X4ADFjjKgQku2KAkD0bIyDYAW4ihhhx6CKKIJJr4CIopIuJwIRAXksfEd9xRx8VpZHzGGWV0" +
            "nMXHWRxVxciGlFzTTTnt1NNPQQ1V1BVHJUVFIDS3+GKMM9Z44xg57tjjj1IEOWSRRyapx5JNsgFllFNycSWWWj5xmpdJIGF1" +
            "EVgXEcTWPPTgtQ4lhF1lCCF8IMHZEjigtgMMgGmEmGSaiaaaObDpJghweiAnnQ3YRrnAaji09lpss9V2W2679fZbcMMVV8Pj" +
            "MkQuwwqUA3rC5RhkfigFnCPgOQKOBiD6DKTryaefgApqAqGGIqooowY4CikAAQEAOw==";

    private const string AnimatedGifBase64 =
            "R0lGODlhIAAgAIcAAO/zgO/ngNvvgPPbgOfbgNvbgMfzgLfzgMPngMfbgLfbgO/LgO+/gNvHgOe3gMfLgLfLgMO/gMO3gKfz" +
            "gKfngJPvgIPzgHfzgH/ngKvbgJ/bgJPbgIPbgHfbgKfLgKe/gJPHgJ+3gIPLgHfLgH+/gH+3gGfzgGfngFPvgEPzgDfzgD/n" +
            "gGvbgF/bgFPbgD/bgCfzgBfzgCPngAf3gAfvgAfngCfbgBfbgAfbgGfLgGe/gFPLgFO/gF+3gEPLgDfLgD+/gD+3gCfLgBfL" +
            "gCO/gCO3gAfLgAe/gAe3gO+rgO+fgNungPOTgOeTgNuTgMergLergMOfgMeTgLeTgO+DgO93gNt/gMeDgLeDgMN3gKergKef" +
            "gJOrgJOfgH+rgH+fgKuTgJ+TgJOTgH+TgKeDgKd3gJODgJN3gH+DgH93gGergGefgFOngEOrgDergD+fgGuTgF+TgFOTgD+T" +
            "gGeDgGd3gFODgFN3gD+DgD93gCergBergCOfgAergAefgCeTgBeTgAeXgAePgCeDgBeDgCN3gAeHgAd/gAd3gO9rgO9fgNtn" +
            "gPNTgOdTgNtTgMdrgLdrgMNfgMdTgLdTgO9DgO83gNs/gMdDgLdDgMM3gKdrgKdfgJNngINrgHdrgH9fgKtTgJ9TgJNTgH9T" +
            "gKdDgKc3gJM/gINDgHdDgH83gO8rgO8fgNsrgNsfgOcXgMcrgLcrgMMfgMMXgO8LgO8AgNsHgMcLgLcLgMMAgKcrgKcfgJMn" +
            "gJ8XgIMrgHcrgH8fgH8XgKcLgKcAgJMHgIMLgHcLgH8AgGdrgGdfgFNrgFNfgD9rgD9fgGtTgF9TgFNTgD9TgGdDgGc3gFND" +
            "gFM3gD9DgD83gCdrgBdrgCNfgAdvgAdngAdfgCdTgBdTgAdTgCdDgBdDgCM3gAdHgAc/gAc3gGcrgGcfgFMngF8XgEMrgDcr" +
            "gD8fgD8XgGcLgGcAgFMHgEMLgDcLgD8AgCcrgBcrgCMfgCMXgAcrgAcfgAcXgCcLgBcLgCMAgAcLgAcAgCH/C05FVFNDQVBF" +
            "Mi4wAwEAAAAh+QQACgAAACwAAAAAIAAgAAAI/wD/CeTXr+C8g/DgvVtorOGwYcIi3sJFsVYtWhj9aSS4b5+8ePESuhtZjBix" +
            "h8FSTrRly+Kslxr98evoEWTCdCRNPuSlkmXLWq5e6ht6r+i9dkjTKU13DphTXlB56ZJFVVYrV1hX5dtqr6s9dmDRoTtH9pfZ" +
            "Xbt0qY3FNlarVqvi4ptbj57dderUiTXH11evXmhzCYb1qjArVqoSz8VX927evX3/Bh5c+NXhxKrKaRbHWZy1z9SoSRudqrQp" +
            "U6VSZ1qdyZKlSrDJyQ4Hrna129OmRduN6tSp06SCY7pE3DWl4+OS07aNWzdv38CFE79k/Pi36966aYfG/dkzZ82ajf8aL0pU" +
            "KFCgJkla78hRI0aMrn/Lvr379/DjR5U/n369pPbvMcLNgNkUmA0zCCqjDDIMfuIgJ5xsImEkFEayyCKKZLjNhthc4+EyICaT" +
            "zDEketJJJxBqoiIkj7R4YSIwaiNjhx+GOGKJJ6a4YouPvAgjIkAWImQheRR5xx11JJnGkmecUcaTWUSZhRVWVGHlIVgSMsiW" +
            "eHRphx10hInGmGaYQcaZWFyhJpVUtGnIm1py6SWYYpJpJppqXsFmm4L0CcgfgM4hqBxyxAEHHGMkKoYYYYABxhRSROqEE00w" +
            "wUQgmP4Z6KCFHproGIs2+mikUkxaKRN+pMrHqny84SobbKz/IesXtHbRxRa4RqFrFEssocSvfQS7hx7EutFGG7CqoawXzHLB" +
            "hRbQQvHEtL0mYW2wfQxb7LGw9rBss1yEEO20T/TqgLVIpFvEukUE4S4PPcSrQwn0hhtCCB9IoK8EDvTrAANHBEzEwEQAYTAP" +
            "POigMAkMgwDCBxBHIHEEDTTAwMVGZDyEEBz/4IMPO+yQw8gjiCCCwx6kDMEDLFe8wMsZG7Fxxx+HPHIOJZ8MQsoerNxyAy8v" +
            "gMPQN9hg9AtIu+BCCyyw0AEHHGywgQYZZKBAAlgXUAABAwwwNA5FH5300k0/HfXUVV+d9dZd1+C2DHDLsMLcKKBwwt0Y5F1B" +
            "BRT0OY3A3wgIIEAAhNNgeAwwJK5CCinUbcLjF1hgwd4TVH6AAZgLDsDmM3SOuOKMOw655JRbjrkBmm8eEAAh+QQBFAAAACwA" +
            "AAAAIAAgAAAINQABCBxIsKDBgwgTKlzIsKHDhxAjSpxIsaLFixgzatzIsaPHjyBDihxJsqTJkyhTqlzJUmRAADs=";

    private const string IcoBase64 =
            "AAABAAIAEBAAAAAAIABgAAAAJgAAACAgAAAAACAAdgAAAIYAAACJUE5HDQoaCgAAAA1JSERSAAAAEAAAABAIAgAAAJCRaDYA" +
            "AAAnSURBVHicY2RhaeBnYBAgGrEw8DOQBFhAukY1EAAso6FEBKB9KAEAMJUCpGkqEYsAAAAASUVORK5CYIKJUE5HDQoaCgAA" +
            "AA1JSERSAAAAIAAAACAIAgAAAPwY7aMAAAA9SURBVHic7dAxCgAwDMNAFwLps/v00g9UUzYd3g1ayelkj63SGVXvxYMfEyET" +
            "IRMhEyETIRMhEyETIROFXG1FAuxcPR8sAAAAAElFTkSuQmCC";

    private const string AvifBase64 =
            "AAAAIGZ0eXBhdmlmAAAAAGF2aWZtaWYxbWlhZk1BMUIAAADrbWV0YQAAAAAAAAAhaGRscgAAAAAAAAAAcGljdAAAAAAAAAAA" +
            "AAAAAAAAAAAOcGl0bQAAAAAAAQAAAB5pbG9jAAAAAEQAAAEAAQAAAAEAAAETAAAASQAAAChpaW5mAAAAAAABAAAAGmluZmUC" +
            "AAAAAAEAAGF2MDFDb2xvcgAAAABqaXBycAAAAEtpcGNvAAAAFGlzcGUAAAAAAAAAIAAAACAAAAAQcGl4aQAAAAADCAgIAAAA" +
            "DGF2MUOBAAwAAAAAE2NvbHJuY2x4AAEADQAGgAAAABdpcG1hAAAAAAAAAAEAAQQBAoMEAAAAUW1kYXQSAAoJGBE/9ogIaDQg" +
            "MjoUx4eGZQIIIJ5FAACLQsvAETezAHa7WqI4BA81IlG3nS/gmwMymHUlbAPSIu1tySK+nmdRFm+0m1YI";

}
