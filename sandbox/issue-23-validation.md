---
owner: platform-team
reviewed: 2026-09-18
review_by: 2027-03-15
---

# Issue #23 sandbox validation

Validated on 2026-09-18 with watcher implementation `71af383`, deployed to the private
`main-watcher-sandbox/main-watcher` replica, against `main-watcher-sandbox/sample-target`.
The trigger worker ran throughout, so every cycle below was dispatched by it.

TS-S10 asks whether a lock issue's team @-mention actually notifies the team, and whether the
CODEOWNERS fallback does the same. It found R-10 in the sandbox: a team mention written by the
App renders as a resolved team link but notifies nobody **unless the App holds organisation
`Members: read`**. With that permission the mention notifies, so R-10 closes as an install
requirement rather than a change to the Reporter.

## The team

`@main-watcher-sandbox/sandbox-owners`, created for this scenario: `privacy: closed` (a secret
team is not mentionable by non-members), `notification_setting: notifications_enabled`, `push`
on `sample-target` (GitHub only notifies a team that can see the repository), one active
member, `pat-actium`. The replica's `targets.yml` carried
`notify: [main-watcher-sandbox/sandbox-owners]` for the whole of part (a), committed as
`e3ccc87`.

## TS-S10 (a): the `notify` team

| Attempt | Red commit | Cycle | Lock | Notification |
| --- | --- | --- | --- | --- |
| 1, App without `Members: read` | `77d1886` 21:16:42Z | [35396099535](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35396099535), check 105764708877 | [sample-target#56](https://github.com/main-watcher-sandbox/sample-target/issues/56) 21:19:45Z | **none** |
| 2, unchanged | `b5255e1` 21:38:45Z | [35398113753](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35398113753), check 105771078639 | [sample-target#57](https://github.com/main-watcher-sandbox/sample-target/issues/57) 21:43:48Z | **none** |
| 3, after granting `Members: read` | `ef6b650` 22:01:52Z | [35399955515](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35399955515), check 105776886950 | [sample-target#58](https://github.com/main-watcher-sandbox/sample-target/issues/58) 22:06:47Z | `team_mention` at 22:07:09Z |

Each lock's body began with `@main-watcher-sandbox/sandbox-owners` on its own line, and none of
them raised the "mention nobody" alert, so the Reporter's `notify` path behaved identically in
all three attempts. Only the App's organisation permission differed.

### What the missing permission looked like

The failure was not in the text. GitHub resolved the mention in #57 to the real team:

```html
<a class="team-mention js-team-mention notranslate" data-id="19576547"
   data-permission-text="Team members are private"
   href="https://github.com/orgs/main-watcher-sandbox/teams/sandbox-owners"
   >@main-watcher-sandbox/sandbox-owners</a>
```

So the lock was readable and clickable; what never happened was the fan-out to members. No
notification and no thread subscription was created for #56 or #57.

The control is in the same notification log, from the same App and repository on the same day —
three locks that mentioned `@pat-actium` individually all notified:

```
20:12:44Z mention  main is broken: tests failed on 579df18   (#54)
19:50:52Z mention  main is broken: tests failed on d6369b6
16:11:58Z mention  main is broken: tests failed on c0779d2
```

An individual mention from an App notifies; a team mention from the same App does not, until the
App can see the organisation's members. `data-permission-text="Team members are private"` is the
visible trace of that: the team's membership was not readable in the App's context, so the
mention could not be expanded into people.

## TS-S10 (b): the CODEOWNERS fallback

Run with `notify: []` on the replica (`f5fce98`, 22:14:17Z), so the Reporter falls back to the
owners of the last `*` rule in the first CODEOWNERS file it finds. The rule named the same team,
which is the stronger case: it exercises the fallback and the team mention together.

| Step | Evidence |
| --- | --- |
| `.github/CODEOWNERS` added while `main` was green, holding `* @main-watcher-sandbox/sandbox-owners` | `1f21209` 22:14:53Z, itself tested green and so the lock's `last_green` |
| Red commit, `failing_tests: ["Beta"]` | `c969ddd` 22:17:12Z |
| Cycle | [35401185951](https://github.com/main-watcher-sandbox/main-watcher/actions/runs/35401185951), check 105780869809 |
| Lock | [sample-target#59](https://github.com/main-watcher-sandbox/sample-target/issues/59) 22:22:47Z, body beginning `@main-watcher-sandbox/sandbox-owners` |
| Notification | `team_mention` |

The replica raised no "Lock issues on … mention nobody" alert for #59, so the Reporter did read
`.github/CODEOWNERS` and did find owners in it: with `notify` empty and no owners, that alert is
what a lock would otherwise have produced.

Both notification rows in this document were read from `GET /notifications`. Their `updated_at`
tracks the thread's newest activity rather than the moment the notification was created, so the
times move as the lock is commented on and closed; the `reason` is the stable evidence.

## Restoration

| Step | Evidence |
| --- | --- |
| `sandbox.json` restored to green after (a) | `ee5ae1f` 22:08:17Z; lock #58 closed 22:12:48Z, `COMPLETED` |
| `sandbox.json` restored to green after (b) | `32c0e67` 22:25:05Z; lock #59 closed 22:29:52Z, `COMPLETED` |
| `.github/CODEOWNERS` deleted, so the target again has none | `efee9e7` 22:30:27Z |
| Replica `targets.yml` back to `notify: [pat-actium]` | `5fca4cb` 22:30:40Z |

The `sandbox-owners` team was left in place; it holds one member and `push` on `sample-target`,
and nothing outside this scenario mentions it.

## R-10

Closed. An App's team @-mention does notify the team, provided the App holds organisation
`Members: read`; without it the mention still renders as a resolved team link, but no member is
notified and no thread subscription is created. This is an install requirement, not a change to
the Reporter: the fallback R-10 offered — expanding a team handle to member logins before
writing the body — would have needed the same permission to enumerate them, and is not needed.

Both halves of TS-S10 pass. The requirement belongs in the App's documented permissions
alongside Contents read, Actions write, Checks write and Issues write, qualified as needed only
where a target's `notify`, or the `*` rule it falls back to, names a team.

## Not exercised in the sandbox

- A CODEOWNERS `*` rule naming an individual user. The file-reading and owner-parsing path is the
  same one #59 exercised, and delivery of an individual mention from the App is shown by the
  three control locks in (a).
- The root and `docs/` CODEOWNERS locations, the empty-first-file precedence rule and the
  last-matching-rule semantics, all covered by unit tests in `MainWatcher.Core.Tests`.
- A team with more than one member, and a member who is not an organisation owner.
