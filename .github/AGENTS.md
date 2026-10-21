# Actions usage controls

Read ACTIONS_USAGE.md before creating or editing workflows here.
Every executable job must have an explicit timeout. Every uploaded artifact must
have explicit retention: one day for build/transfer files, up to three days for
failure diagnostics. Cancel superseded validation runs, while allowing active
publication to finish. Preserve project-specific release authorization and gates.
Do not add full packaging matrices to routine feature-branch pushes or duplicate
the same product build across jobs. Validate workflow syntax before committing.
