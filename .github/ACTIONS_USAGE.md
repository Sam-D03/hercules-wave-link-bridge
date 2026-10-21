# GitHub Actions usage policy

Apply this policy to new and changed workflows in every project. The account's free minutes and artifact allowance are shared across private repositories.

## Storage
- Keep published installers and downloadable products in Releases or the existing release host. Preserve immutable releases.
- Use Actions artifacts only for job-to-job transfer and short-lived failure evidence.
- Set explicit retention: 1 day for transfer/build packages; 3 days for failure diagnostics.
- Upload diagnostics only on failure; avoid full build trees, dependencies, duplicated executables and successful-test screenshots.
- After publishing a release, verify its expected asset names and sizes before deleting only that run's transfer artifact. Keep a one-day expiry as fallback.
- Never delete other runs, published releases, caches, user files or production data as a side effect of a normal build.
- Repository-level artifact/log retention should be 3 days, with explicit 1-day overrides for packages. This default affects future uploads; existing artifacts need separate review.

## Minutes
- Run pull-request validation and main-branch validation. Avoid also running the same expensive workflow on every feature-branch push.
- Cancel obsolete pull-request runs using a workflow-specific concurrency group. Never interrupt an active publication mid-upload.
- Give every job an explicit timeout: 10 minutes for lint, 15 for small builds, 35 for Windows validation; justify larger exceptions.
- Build each product once per run and share the output. Keep native Windows tests on Windows; use Linux for portable lint and tests after verifying platform assumptions.
- Package and publish only for a release/manual request or the project's existing approved release trigger. Preserve release gates and deployment semantics.
- Restrict full architecture matrices to release builds when routine validation can use one representative platform.
- Cache dependency downloads against lockfiles. Caches have a separate quota; do not use caches as release storage.
- Set Docker build-record retention explicitly (1 day) or disable build-record upload when unused.

## Budget and review
- Keep the account's $0 hard budgets; enable GitHub's included-usage alerts at 90% and 100%.
- Review billing at 70% of the monthly minutes allowance (1,400 / 2,000 today), before the hard limit. This is a review threshold, not an automatic scheduler.
- Aim for under 250 MB average retained artifact storage across projects, leaving headroom under the current 500 MB allowance.
- Forecast monthly storage as sum(size in GB * retention hours) / hours in billing month. Short retention reduces future accrual; deleting files does not undo accrued GB-hours.
- Lint changed workflows with actionlint and inspect triggers, permissions, artifact consumers and publication behavior before merging.
- Retention and concurrency reduce waste; they cannot guarantee that arbitrary build volume stays within a finite free allowance.
