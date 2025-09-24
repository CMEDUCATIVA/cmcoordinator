# OpenProject Revit Add-In

## Intro

**This Software is still in Alpha status and not yet officially released. Please use it at your own risk.**

The _OpenProject Revit Add-In_ allows you to use the open source project management software _OpenProject BIM_ directly
within your Autodesk Revit environment. It lets you create, inspect and manage issues right in the moment when you can
also solve them - when you have your Revit application fired up and the relevant BIM models open. Issues get stored as
BCFs centrally and are available to every team member in real time - thanks to our browser based IFC viewer even to
those team members without expensive Revit licenses. No BCF XML import/export is needed. However, you still can import
and export BCF XML as you like and stay interoparable with any other BCF software.

This program originally based on the excellent [BCFier](https://github.com/teocomi/bcfier) but then moved into a new
direction.

## Installation

Please follow the [installation instructions](docs/installation-instructions.md).

## Reporting bugs

You found a bug? Please [report it](https://docs.openproject.org/development/report-a-bug) to our [OpenProject community](https://community.openproject.com/projects/revit-add-in). Thank you!

## Contribution and development

OpenProject is supported by its community members, both companies and individuals.

We are always looking for new members to our community, so if you are interested in improving the OpenProject Revit Add-in we would be glad to welcome and support you getting into the code. There are guides as well, e.g. a [Quick Start for Developers](https://www.openproject.org/development/setting-up-development-environment/), but don't hesitate to simply [contact us](https://www.openproject.org/contact-us) if you have questions.

Working on OpenProject comes with the satisfaction of working on a widely used open source application.

Also, if you do not want to be limited to working on open source in your free time, OpenProject GmbH, the company contributing to the OpenProject development, [is hiring](https://www.openproject.org/career/).

### Browser Developer Tools

We can enable the **Developer Tools** for the `CefSharp` browser of the Windows application. The add-in creates on first
run a default configuration file at `~\AppData\Roaming\OpenProject.Revit\OpenProject.Configuration.json`. To enable the
developer tools, change the value of `EnableDevelopmentTools` to `true`.

## Contact

Here you can find our [contact information](https://www.openproject.org/contact-us).

## Security / responsible disclosure

We take security very seriously at OpenProject. We value any kind of feedback that
will keep our community secure. If you happen to come across a security issue we urge
you to disclose it to us privately to allow our users and community enough time to
upgrade. Security issues will always take precedence over anything else in the pipeline.

For more information on how to disclose a security vulnerability, [please see this page](docs/development/security/README.md).

## License

GNU General Public License v3 Extended This program uses the GNU General Public License v3, extended to support the use
of BCFier as Plugin of the non-free main software Autodesk Revit.
See <http://www.gnu.org/licenses/gpl-faq.en.html#GPLPluginsInNF>.

Copyright (c) 2013-2016 Matteo Cominetti

This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
License as published by the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied
warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

You should have received a copy of the GNU General Public License along with this program. If not,
see <http://www.gnu.org/licenses/gpl.txt>.
