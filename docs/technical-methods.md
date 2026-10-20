# Technical calculation methods and evidence

Reviewed on **2026-10-20**. This document describes the independently authored calculation kernels in `src/FirstBlick.Core/Technical`. It is also the evidence register for the initial DP-10 and DP-11 work.

## Implemented methods

| Method ID | Implemented calculation | Explicit boundary |
|---|---|---|
| `solid-plane-resistance` | Series thermal resistance, transmittance, signed heat flux and interface temperatures for solid homogeneous layers | Constant conductivity, steady one-dimensional conduction; no air layers, ventilation, moisture, lateral heat transfer, fasteners, glazing or soil coupling |
| `ideal-independent-parallel-paths` | Area-weighted conductance of independent one-dimensional paths | No lateral heat exchange. This does **not** implement the ISO 6946 combined method and is not a normative rafter-roof calculation |
| `supplied-thermal-bridge-aggregation` | Adds supplied linear and point thermal-bridge contributions to a supplied base heat-loss coefficient | Does not calculate psi/chi coefficients, temperature fields or surface-condensation risk |
| `rectangular-roof-plane` | Sloping length, pitch, projected area, surface area and perimeter of one rectangular plane | Horizontal run and rise are explicit; no implicit second roof side, overhang or opening |
| `simple-roof-plane-polygon` | Gross/net area and boundary lengths for a simple polygon with disjoint interior openings | Coordinates lie on the actual roof plane. No map projection, pitch correction, contractual measurement deduction or waste allowance |

All result records carry the normalized input, method ID, method version (`1.0.0`), SHA-256 of the normalized input JSON, explicit limitations and `isNormative: false`. The digest identifies the supplied calculation evidence; it is not a signature or a statement that the supplied material data are correct. A changed input or value-source reference changes its digest. Caller-owned input collections are copied before returning a result.

### Heat transfer

For layers ordered from inside to outside, thickness `d` is in metres and conductivity `lambda` is in W/(m K). The independent elementary model is:

```text
R_layer = d / lambda
R_total = R_inside_surface + sum(R_layer) + R_outside_surface
U = 1 / R_total
q = (T_inside - T_outside) / R_total
T_interface = T_inside - q * R_cumulative
```

`R` is in m² K/W; `U` is in W/(m² K); `q` is in W/m². Positive flux is outward. Zero surface resistances are supported as explicit ideal boundary conditions. No default design conductivity or surface resistance is inferred. Every material layer requires a user-supplied `ValueSource` reference. The series-resistance method follows the elementary steady conduction model described in the [US Department of Energy heat-transfer handbook](https://www.energy.gov/documents/doe-hdbk-1012-92vol2), chapter “Conduction Heat Transfer”. This is a mathematical model, not a claim of complete building-standard implementation.

Independent parallel paths use `U_equivalent = sum(area_fraction * U_path)` and `R_equivalent = 1 / U_equivalent`. All paths share their boundary temperatures and surface resistances. Area fractions must sum to one within an absolute tolerance of `1e-10`; only that numerical tolerance is normalized. Each path retains its own input and result. This model is useful for an idealized comparison, but omits lateral heat spreading between real rafters and insulation.

Bridge aggregation uses `H = U * A + sum(psi * length) + sum(chi * count)`, where `H` is in W/K. The equivalent transmittance is `H/A`; it is an area-normalized aggregate. Coefficient sources and a common dimensional reference convention are required. Signed coefficients are retained, but an input producing non-positive total `H` is rejected. The caller must establish compatible dimensions and avoid counting the same thermal bridge twice. The kernel does not perform the numerical geometrical modelling described by [ISO 10211:2017](https://www.iso.org/standard/65710.html).

### Geometry

For a rectangular plane with ridge-parallel length `l`, horizontal run `r` and rise `h`, the sloping length is `sqrt(r² + h²)`. Its surface area is `l * sqrt(r² + h²)`; the horizontal projected area is `l * r`. The method describes **one** plane.

General polygons use local coordinates in metres on the actual plane and the signed cross-product area formula. Ring orientation does not change the area. An optional duplicate closing vertex is removed. The algorithm translates coordinates to the first vertex before calculating area to reduce cancellation for coordinates far from the origin.

The polygon validator rejects repeated/degenerate edges, self-intersection, self-touching, adjacent backtracking and areas below `1e-8 m²`. Openings must be strictly inside the outer ring, must not cross its concave boundary, and must not overlap, touch or nest. A maximum of 2,000 vertices across the complete request bounds intersection work. Contractual rules for whether an opening should be deducted must be implemented separately from this geometric quantity.

## Reference checks

`tests/FirstBlick.Core.Tests/Technical` contains independently specified elementary references and adversarial boundary cases:

| Reference | Expected result |
|---|---|
| Solid layers: `0.1 m / 0.04 W/(m K)` and `0.02 m / 0.2 W/(m K)`; explicit surface resistances `0.13` and `0.04` | `R_total = 2.77 m² K/W`, `U = 0.36101083032490975 W/(m² K)` |
| Same stack with inside/outside temperatures `20 °C / -10 °C` | `q = 10.830324909747291 W/m²`; boundary temperature drops conserve the complete `30 K` difference |
| Two ideal paths, resistances `4` and `1`, fractions `0.75` and `0.25` | `U_equivalent = 0.4375 W/(m² K)` |
| Supplied base `A = 100 m²`, `U = 0.2`; linear `psi = 0.05`, length `20 m`; five points of `chi = 0.2` | `H = 22 W/K`, `H/A = 0.22 W/(m² K)` |
| One rectangular plane with ridge length `12 m`, run `4 m`, rise `3 m` | Sloping length `5 m`, projected area `48 m²`, surface area `60 m²`, perimeter `34 m` |
| Actual-plane rectangle `12 × 10 m`, interior opening `2 × 3 m` | Gross area `120 m²`, opening `6 m²`, net area `114 m²` |

Further checks cover equal/reversed boundary temperatures, splitting a layer, identical parallel paths, input evidence, caller mutation, NaN/infinity, invalid dimensions, signed bridge corrections, polygon orientation/translation, concavity, hole overlap and the request budget. These tests verify the stated models. They are not independent verification of a complete DIN/ISO method or site-specific design.

## Standards and data boundaries

The following primary catalogue pages were checked on 2026-10-20. Only their publicly available metadata and scope summaries were used; no licensed full standard or manufacturer catalogue was imported.

| Area | Verified source and consequence |
|---|---|
| Thermal resistance/transmittance | [ISO 6946:2017](https://www.iso.org/standard/65708.html) is listed as current, confirmed in 2022, with corrected English version 2021-12. It includes homogeneous-layer methods and a bounded approximate inhomogeneous method; most cases of metal bridging through insulation fall outside its scope. The kernels above do not claim to implement that complete method. |
| Moisture protection in Germany | [DIN 4108-3:2024-03](https://www.dinmedia.de/de/norm/din-4108-3/373925063) is listed as current. The public scope describes three assessment levels: permitted constructions without calculation, period balance, and hygrothermal simulation. The first two levels apply only to eligible constructions and uses; the period-balance height limit is 700 m. |
| Simplified humidity/condensation | [ISO 13788:2012](https://www.iso.org/standard/51615.html?browse=tc) is listed as current, confirmed in 2023. Its simplified moisture model excludes moisture-dependent material properties, capillary transport, air movement through gaps and hygroscopic storage. No full condensation/drying calculation is currently implemented. |
| Thermal bridges | [ISO 10211:2017](https://www.iso.org/standard/65710.html) specifies 2D/3D geometrical modelling for heat flows and minimum surface temperatures. Adding externally supplied coefficients is only an aggregation step. |

The presence of a standard identifier in a result or report must never turn `isNormative` into `true`. A normative method needs a separately implemented and versioned specification, cleared data rights, complete applicability checks and independent reference cases, including rejection cases. Current work therefore leaves the following open:

- Full ISO 6946 treatment of inhomogeneous layers, air layers and applicable corrections.
- DIN 4108-3 / ISO 13788 condensation and drying, including complete method selection and rejection boundaries.
- Hygrothermal simulation; calculated psi/chi values; structural, wind, snow and fastening verification.
- Main and emergency drainage dimensioning with appropriate rainfall/location data and applicable rules.
- Manufacturer-specific tapered-insulation products, nesting and installation schedules.
- Photovoltaic structural/electrical planning and third-party technical-software interfaces.

This is a method-scope record for open-source development. It is not a production-release or legal-compliance approval process.

## Attached demonstration installers: static evidence

The following five attachments were available and read as binary data. No attachment was executed, and no proprietary code, asset, catalogue or help-document text was included in FirstBlick.

| Attachment | Bytes | SHA-256 |
|---|---:|---|
| `daemmdemo.exe` | 277900706 | `d415a91a1b7f1ee5febac9dea5c719a9c709178d28f457a2d30fe4baec8ab9cb` |
| `handwerkdemo.exe` | 362714168 | `aa33eb1877c1b28347b56b00b74a52ca9481d44605e313c53fd33e5daefbf6bc` |
| `dachdemo.exe` | 361445499 | `a83535bbcf3936bec0588f2149cf66c9e3822c60b4a52d730a6afd8888f97bce` |
| `flachdach-demo.exe` | 290283372 | `5cf35fa3dc5396973f155815335c8aa3e35e83d92d56e466b3971f3ec26ed94b` |
| `bauphysik-demo.exe` | 278520040 | `ac2e91b32925f1a380a73191c7670f2d569a4f8b6e355ab53ca10657cba44500` |

All five contain a PE32 x86 GUI installer stub with five sections and an appended payload. Static length-prefixed filename detection found program/library entries and 18 PDF-name entries in each package; the flat-roof package also contains names indicating roof-construction definitions, and the physics package names indicating component data. These observations establish package contents only at the filename level. They do not establish that a named payload was successfully extracted, that a calculation ran, which algorithm it used, or whether its output is correct.

`drain-demo.exe`, `dd-demo.exe` and `steildach-demo.exe` were not transferred into the workspace and have not been analyzed. Their absence is recorded independently of overlapping help filenames inside the other installers.

The vendor's [public physics-software page](https://www.mf-dach.de/software/bauphysiksoftware) describes U-value and moisture calculations and a component library. That is a functional reference, not permission to redistribute its software or data, and not an independent validation of FirstBlick. The implementation above derives its elementary equations independently and ships no vendor material library.
