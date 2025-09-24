# Changelog

This is the Changelog for the OpenProject Revit Add-in. It follows the guidelines described
in https://keepachangelog.com/en/1.0.0/. The versions follow [semantic versioning](https://semver.org/).

## Unreleased

### Added

- Amended README with information about reporting bugs and other useful information

### Changed

- Viewpoint snapshot data is given in an improper state to the OpenProject instance frontend. The current hack has to be
  maintained until the related
  [work package](https://community.openproject.org/projects/bcfier/work_packages/39135/activity) is resolved and the
  solution deployed.

### Fixed

- Section boxes with infinity values, which can exist after importing viewpoints with less then 6 clipping planes, no
  longer create invalid viewpoint data when generating a new viewpoint.
- If there is a newer version of the AddIn released at GitHub, a notification dialog is displayed again
- When exporting a viewpoint from a Revit view, elements hidden by category are interpreted as hidden. However, applying
  this viewpoint does not apply the category visibility, but sets all contained elements to hidden, as BCF viewpoints
  are not aware of Revit categories.

## [2.3.1] - 2021-10-04

### Fixed

- Fixed an issue that led to an exception when installing the Revit Add-In for the very first time
- Made the loading of the `OpenProject.Configuration.json` more robust, so that missing or outdated keys no longer lead
  to errors
- Improved logging and error dialog communication

## [2.3.0] - 2021-09-21

### Added

- Clipping plane direction is now specified according to the conventions of the BCF 3.0. Objects on the positive side of
  the clipping plane are invisible, while objects on the negative side are rendered visible.
- OpenProject Revit Add-in installer now contains the additional option to install the add-in into Revit 2022.
- Downloading BCF issues now is possible from within the embedded browser in the Revit add-in. A file save dialog is
  opened for selecting the download destination.
- The runtime now does rudimentary logging. The logfiles are rolled over on a daily basis and are located in
  the `%APPDATA%\OpenProject.Revit\logs` folder next to the configuration files.

### Changed

- Importing any viewpoints (including those created by OpenProject BIM Edition's BCF viewer) do now try to import the
  clipping planes of the viewpoint into Revit section boxes. Only clipping planes that are within 5 degrees of a
  coordinate plane are considered valid input.
- Viewpoints can now only be created when having an active 3D view in Revit.
- OpenProject viewpoints are now opened in a dedicated view, `OpenProject Perspective` for viewpoints with a perspective
  camera and `OpenProject Orthogonal` for viewpoints with a orthogonal camera.

### Fixed

- Instance validation with url schema prefix and without api path suffixes are now validated correctly in all
  combinations.
- Login via **Azure AD** or **Google** now works properly within the embedded browser.
- Initial configuration of the OpenProject Revit Add-in no longer sets the developer tools of the embedded browser to
  activated. In addition there is no longer a dummy instance added to configuration on first install.
- Restoring an orthogonal viewpoint now scales the view correctly to the previously recorded view box height.
- Loading times for viewpoints were slightly improved, on machines without enough capability the asynchronous zoom for
  orthogonal viewpoints can take several seconds (see this
  [official issue](https://thebuildingcoder.typepad.com/blog/2020/10/save-and-restore-3d-view-camera-settings.html)).
- An issue was fixed, causing the cursor in input fields in the embedded browser vanishing occasionally.

## [2.2.5] - 2021-04-16

### Added

- Initial state of changelog