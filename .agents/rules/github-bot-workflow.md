# Git & GitHub Workflow as Agent

When asked to implement changes and propose them on GitHub (commits, pushing branches, opening/updating Pull Requests, or commenting on Issues/PRs), follow these rules unless the user explicitly asks to act under their personal account:

1. **Never commit or push directly to `main`.**
   - Always create a descriptive branch from `main` (e.g., `ai/<task>`, `feat/<task>`, or `fix/<task>`).

2. **Use standard `git` commands (or an available Git MCP server, if configured) for commits and pushes:**
   - Rely on the repository's local `.git/config` identity and credential configuration so `git commit` and `git push` author and push changes under the agent identity.
   - Optionally include a commit trailer (e.g., `Generated-by: Antigravity`) or note in the PR body.

3. **Use an agent-scoped GitHub MCP server (if available) for Pull Requests and Issues:**
   - Prefer a GitHub MCP server authenticated as the agent/bot for write operations (creating/updating PRs, commenting on Issues/PRs), rather than a personal human-account GitHub MCP server.
   - If no agent-scoped GitHub MCP server is available, use the configured local CLI/helper workflow instead of a personal account.
   - `.github/CODEOWNERS` assigns reviewers automatically, so no manual reviewer assignment is needed unless requested.

4. **Never merge.** Stop after creating/updating the Pull Request and share the PR link with the user for review and approval.
