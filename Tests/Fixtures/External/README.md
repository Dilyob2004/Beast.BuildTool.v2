# External project fixture

`SDK` is an ordinary Beast project with a static library target (`SDK`: modules `Widgets` -> `Core`).
`AppA` and `AppB` use it through `externalProjects` in their `Config/BuildConfig.json`. Tests copy
this directory and build both applications (in parallel); the SDK tree must stay free of build files.
