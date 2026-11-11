# Security

MoonWalk is a **local, single-user tool**. Its HTTP API has no authentication: anyone who can reach the port can start
agents, change files in the repositories you configured, and delete runs.

- By default it listens only on `http://localhost:5870`. Do not expose it to a network, a tunnel or a public interface.
- Agents run with your user's permissions, in the repositories named in your project profiles. Use the profile `guards`
  (deny paths, size limits) and read each run's Spec before approving it.
- Keep `projects/*.local.json`, `agents.json`, `presets.json`, `briefs/` and `runs/` out of version control (they are
  git-ignored). Briefs and run events are stored in plain text, so do not put secrets in a brief.

## Reporting a vulnerability

Please open a private security advisory on the GitHub repository (Security tab, "Report a vulnerability") instead of a public
issue. Include the version or commit, what you did and what happened.
