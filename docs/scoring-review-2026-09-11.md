# Feedback scoring review — 11 September 2026

The review found three concrete problems:

- Similarity matching discarded words appearing in more than 5% of rated stories and phrases appearing in more than 15%, subject to small-history allowances. The classifier independently discarded features appearing in more than 35%. A frequently rated interest could therefore lose influence as its history grew, regardless of how consistently it was liked or disliked.
- Single-article similarity scoring excluded a computed article ID before grouping duplicates, while batch scoring excluded the entire story group by feed and external ID. These paths could produce different automatic predictions. The ensemble also fetched feedback separately for its component models and calibration.
- One-sided feedback could calibrate a confident decision for a completely unrelated article. A regression case with 24 positive, featureless examples produced a score of approximately 0.744 instead of a neutral 0.5.

## Changes

Frequent similarity features now survive the frequency limit when their weighted votes have at least 50% directional agreement and their smoothed occurrence rate in one class is at least twice the other class's rate. The comparison accounts for the overall amount of positive and negative feedback and excludes the article's own story. Both classes must be present, and the class contrast must agree with the weighted vote direction. Existing rare-feature, duplicate, source and boilerplate safeguards remain in place.

The classifier keeps recurring features based on their positive/negative contrast rather than imposing an additional frequency ceiling. It returns a neutral prediction when an article has no features in the trained vocabulary. Calibration requires both vote directions and excludes exactly neutral predictions from decision thresholds.

Single and batch scoring now share the same exclusion path. Similarity, classifier training and calibration all use the same feedback snapshot. Vote changes and undo invalidate the existing model fingerprint. Manual article choices and explicit avoided-topic rules retain their existing behavior.

## Historical replay

A local snapshot contained 779 current votes: 429 positive and 350 negative, including strong votes. Feedback was ordered by its saved timestamp. Two evaluations trained on the earlier 60% or 80% and predicted the following 20% (156 articles each). Earlier copies of validation stories were excluded using the production duplicate grouping. Future votes and explicit avoided-topic rules were not supplied to training or prediction. The live database was not modified during this evaluation.

| Earlier training votes | Later evaluation votes | Ranking AUC before → after | Mean squared score error before → after | Correct High / all High before → after | Correct Filtered / all Filtered before → after |
|---|---|---|---|---|---|
| 467 | 156 | 0.756 → 0.760 | 0.22127 → 0.22025 | 19/24 → 22/27 | 6/6 → 10/11 |
| 623 | 156 | 0.712 → 0.731 | 0.22137 → 0.21958 | 29/34 → 32/38 | 3/4 → 2/3 |

Higher AUC means liked stories tend to rank above disliked ones; lower squared error means scores are closer to the later vote direction. Both improved modestly in both periods. Across the 312 evaluated articles, automatic decisions increased from 68 to 79, with correct decisions increasing from 57 to 66. Their combined precision was approximately 83.8% before and 83.5% after: this is an improvement in ranking and coverage, not evidence of higher decision precision or of achieving 90% precision on future articles.

These are development comparisons on two portions of one user's history, not an untouched final test set. Only the current saved choice is available for each article, so this cannot reconstruct all historical changes or undone votes. Rated articles are also a selected sample of the inbox. Strong votes retain double training weight; evaluation treats either positive button as positive and either negative button as negative.

## Verification

`dotnet test PersonalRSS.sln --no-restore` passed all 37 tests. New coverage checks neutral fresh installations independently learning opposite preferences, recurring interests, neutral shared phrases, one-sided and highly skewed histories, consistent single/batch duplicate exclusion, one feedback read per request, and model invalidation after changed or undone feedback. Existing tests cover explicit topic overrides, manual-score preservation, boilerplate, class balancing and relevance bands.

The private replay input, runner and raw metrics remain under the Git-ignored `data/scoring-review` directory. No article titles, feed subscriptions or individual votes are included in this report.

The tested build was deployed locally after a verified SQLite backup. All 17 feeds refreshed successfully, and the rescore endpoint updated 2,559 stored articles. Post-deployment checks confirmed database integrity, zero feed errors, and unchanged existing votes, manual scores, read states and avoided-topic rules.
