# Security policy

Rollcall holds standing admin rights over a Microsoft 365 tenant, so we treat security reports as the highest priority.

## Report a vulnerability

Report vulnerabilities privately through GitHub: open the repository's **Security** tab and choose **Report a vulnerability**. Do not open a public issue, pull request, or discussion for a security problem.

Include:

- What the issue is and what an attacker could do with it.
- The version or commit you tested.
- Steps to reproduce, or a proof of concept.

We aim to acknowledge a report within 3 working days and to agree a disclosure date with you once we understand the impact. We credit reporters in the release notes unless you ask us not to.

## Supported versions

Rollcall has not had a release yet. Once v1.0.0 ships, security fixes go to the latest minor release.

## Scope

In scope: the portal, API, job worker, AD agent, install script, and container images published from this repository. Out of scope: vulnerabilities in Microsoft 365 itself; report those to the [Microsoft Security Response Center](https://msrc.microsoft.com/).
