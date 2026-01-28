# Contributing

Thanks for your interest in contributing to Fallen-Networks! :tada:

- [Commit Message Guidelines](#commit-message-guidelines)
  * [Commit Message Format](#commit-message-format)
  * [Revert](#revert)
  * [Type](#type)
  * [Scope](#scope)
  * [Subject](#subject)
  * [Body](#body)
  * [Footer](#footer)
  * [Examples](#commit-examples)
- [Branch Guidelines](#branch-guidelines)
  * [Branch Format](#branch-format)
  * [Examples](#branch-examples)
- [Creating an Issue](#creating-an-issue)
  * [Creating a Good Issue Reproduction](#creating-a-good-issue-reproduction)
- [Creating a Pull Request](#creating-a-pull-request)
  * [Requirements](#requirements)
  * [Setup](#setup)
  * [Submit Pull Request](#submit-pull-request)
- [License](#license)

## Commit Message Guidelines

We have very precise rules over how our git commit messages should be formatted. This leads to readable messages that are easy to follow when looking through the project history. We also use the git commit messages to generate our [changelog](https://github.com/Fallen-Networks/CS2-RampFix/blob/main/CHANGELOG.md). Our format closely resembles Angular's [commit message guidelines](https://github.com/angular/angular/blob/main/CONTRIBUTING.md#commit).

### Commit Message Format

We follow the [Conventional Commits specification](https://www.conventionalcommits.org/). A commit message consists of a **header**, **body** and **footer**.  The header has a **type**, **scope** and **subject**:

```
<type>(<scope>): <subject>
<BLANK LINE>
<body>
<BLANK LINE>
<footer>
```

The **header** is mandatory and the **scope** of the header is optional. **Body** and **footer** messages are optional if your commit message contains a short yet concise description.

### Revert

If the commit reverts a previous commit, it should begin with `revert: `, followed by the header of the reverted commit. In the body it should say: `This reverts commit <hash>.`, where the hash is the SHA of the commit being reverted.

### Type

If the prefix is `feat`, `fix` or `perf`, it will appear in the changelog. However if there is any [BREAKING CHANGE](#footer), the commit will always appear in the changelog.

While we prioritize the first four commit types listed below, you are allowed to use any of the following:

* **feat**: A new feature
* **fix**: A bug fix
* **refactor**: A code change that neither fixes a bug nor adds a feature
* **chore**: Changes to the build process or auxiliary tools and libraries such as documentation generation
* **docs**: Documentation only changes
* **style**: Changes that do not affect the meaning of the code (white-space, formatting, missing semi-colons, etc)
* **perf**: A code change that improves performance
* **test**: Adding missing tests

### Scope

The scope can be anything specifying place of the commit change. Usually it will refer to a component but it can also refer to a utility. For example `core`, `plugins`, `plugin-name`, `assets`, `inventory`, etc. If you make multiple commits for the same component, please keep the naming of this component consistent. For example, if you make a change to admin and the first commit is `fix(admin)`, you should continue to use `admin` for any more commits related to administration. As a general rule, if you're modifying a component use the name of the folder.

### Subject

The subject contains a succinct description of the change:

* use the imperative, present tense: "change" not "changed" nor "changes"
* do not capitalize first letter
* do not place a period `.` at the end
* entire length of the commit message must not go over 50 characters
* describe what the commit does, not what issue it relates to or fixes
* **be brief, yet descriptive** - we should have a good understanding of what the commit does by reading the subject

### Body

The body is completely optional so long as your commit message contains a short yet concise description
Just as in the **subject**, use the imperative, present tense: "change" not "changed" nor "changes".
The body should include the motivation for the change and contrast this with previous behavior.

### Footer

The body is completely optional so long as your commit message contains a short yet concise description and you **aren't resolving a GitHub issue with the commit.**
The footer should contain any information about **Breaking Changes** and is also the place to reference GitHub issues that this commit **Closes**.

**Breaking Changes** should start with the word `BREAKING CHANGE:` with a space or two newlines. The rest of the commit message is then used for this.

### Commit Examples

Appears under "Features" header, admin subheader:

```
feat(admin): add 'admin menu'
```

Appears under "Bug Fixes" header, admin subheader, with a link to issue #28:

```
fix(admin): use kick instead of ban

closes #28
```

Appears under "Performance Improvements" header, and under "Breaking Changes" with the breaking change explanation:

```
perf(chat): replaced the way chat messages are sent

BREAKING CHANGE: The chat messages were originally sent by hooking an internal service, they now require an outside plugin for better performance.
```

Appears under "Breaking Changes" with the breaking change explanation:

```
refactor(permissions): update to new permissions system

BREAKING CHANGE:

Removes the old permissions system to use the new Fallen permissions.
```

The following commit and commit `667ecc1` do not appear in the changelog if they are under the same release. If not, the revert commit appears under the "Reverts" header.

```
revert: feat(admin): use kick instead of ban

This reverts commit 667ecc1654a317a13331b17617d973392f415f02.
```

## Branch Guidelines

We have very precise rules over how our git branches should be formatted. This leads to readable git branches that are easy to follow when looking through the project itself. Our branch guidelines closely resemble our commit guidelines, but with slightly different formatting. Please ensure your branches are created in relation to the commits you are creating, and closely follow the scope of the overall commit list you plan on creating.

### Branch Format

As stated above, created branches should be following a similar format to conventional commits, in that a branch contains a **header**, **scope**, and a **subject** like below: 

```
<type>\<scope>\<subject>
```

Unlike commits, the **header** and **scope** are both mandatory, while the subject is optional depending on what you are working on, and the scope should closely resemble an overall feeling of what you are trying to accomplish and commit.

### Branch Examples

You are adding features to the entire admin folder with multiple plugins inside of it:

```
feat/admin
```

You are working on only the admin menu plugin inside of the admin folder:

```
feat/admin/admin-menu
```

You are fixing several bugs with the admin folder:

```
fix/admin
```

You are fixing bugs only in the admin menu inside the admin folder:

```
fix/admin/admin-menu
```

## Creating an Issue

* If you have a question about using the repository, please ask on the [Forums](http://fallen-networks.com/) or in the [Discord](https://discord.gg/fallen).

* It is required that you clearly describe the steps necessary to reproduce the issue you are running into. Although we would love to help you out as much as possible, diagnosing issues without clear reproduction steps is extremely time-consuming and simply not sustainable.

* The issue list of this repository is exclusively for bug reports and feature requests. Non-conforming issues will be closed immediately.

* Issues with no clear steps to reproduce will not be triaged. If an issue is labeled with "needs: reply" and receives no further replies from the author of the issue for more than 14 days, it will be closed.

* If you think you have found a bug, or have a new feature idea, please start by making sure it hasn't already been [reported](https://github.com/Fallen-Networks/CS2-RampFix/issues?utf8=%E2%9C%93&q=is%3Aissue). You can search through existing issues to see if there is a similar one reported. Include closed issues as it may have been closed with a solution.

* Next, [create a new issue](https://github.com/Fallen-Networks/CS2-RampFix/issues/new/choose) that thoroughly explains the problem. Please fill out the populated issue form before submitting the issue.


## Creating a Good Issue Reproduction

### What is an Issue Reproduction?

A reproduction is a detailed process of producing the issue you are currently having in the most up to date **stable** version of the repository.

### Why Should You Create a Reproduction?

A reproduction of the issue you are experiencing helps us better isolate the cause of the problem. This is an important first step to getting any bug fixed!

Without a reliable reproduction, it is possible we will be unable to resolve the issue, leading to it being closed. In other words, creating a reproduction of the issue helps us help you.

### How to Create a Reproduction

* Create a new server using our most up to date **stable** release.
* Thoroughly test the issue to consistently reproduce the steps, while also creating a video demonstration.
* Be sure to include steps to reproduce the issue. These steps should be clear and easy to follow.
* Post the steps and video inside the issue you create.

## Creating a Pull Request

Before creating a pull request, please read our requirements that explains the minimal details to have your PR considered and merged into the codebase.

### Requirements
1. PRs must reference an existing issue that describes the issue or feature being submitted.
2. PRs must include tests covering the changed behavior or a description of why tests cannot be written.
3. PRs must follow the commit format and pass all workflow checks, usually shown with a checkmark or a red x on each commit.
4. PRs must not be large multi level changes and should instead be focused on a small set of components of the same type.

> Note: We appreciate you taking the time to contribute! Before submitting a pull request, please take the time to comment on the issue you are wanting to resolve. This helps us prevent duplicate effort or advise if the team is already addressing the issue.

* Looking for an issue to fix? Look through our issues with the [help wanted](https://github.com/Fallen-Networks/CS2-RampFix/issues?q=is%3Aopen+is%3Aissue+label%3A%22help+wanted%22) label!

### Setup

**Please ensure you follow [Commit Message Guidelines](#commit-message-guidelines) as well as [Branch Guidelines](#branch-guidelines)**

Conventional Commit Validation:
1. [Download the installer](https://nodejs.org/) for the LTS version of Node.js. This is the best way to also [install npm](https://blog.npmjs.org/post/85484771375/how-to-install-npm#_=_).
2. Run `npm install` to install the commit pre checker.

Project Setup:
1. Create a branch based upon the `dev` branch using the [branch guidelines](#branch-guidelines).
2. Navigate into the directory of the component you wish to change.
3. Create, test, and commit your changes to your branch.
4. Ensure your commits pass the workflow checks and have good quality commits.
5. Test again, as we want to prevent all bugs possible.

### Submit Pull Request

1. [Create a new pull request](https://github.com/Fallen-Networks/CS2-RampFix/compare) with the `dev` branch as the `base`. You may need to click on `your branch` to find your changes.
2. See the [Creating a pull request](https://docs.github.com/en/pull-requests/collaborating-with-pull-requests/proposing-changes-to-your-work-with-pull-requests/creating-a-pull-request) GitHub help article for more information.
3. Please fill out the provided Pull Request template to the best of your ability and include any issues that are related.
4. Please ensure you leave spaces in between [ ] for empty boxes, and filled boxes should look like [x]

### Review Process for Feature PRs

All pull requests will be reviewed by a Director of Fallen-Networks and be ran through a rigorous design process, which must be completed before the PR can be reviewed or merged. As a result of the design process, community feature PRs are subject to large changes. In some cases, the team may instead create a separate PR using pieces of the community PR. Either way, you will always receive co-author commit credit when the feature is merged.

To expedite the process, please ensure that all feature PRs have an associated issue created, with a clear use case for why the feature should be added.


## License

By contributing your code to the Fallen-Networks/CS2-RampFix GitHub Repository, you agree to allow Fallen-Networks LLC unlimited use to all of your contribution.
