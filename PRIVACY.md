# Privacy policy — NextraRhinoGH

Effective date: October 28, 2026

## Scope and publisher

This policy describes the standard NextraRhinoGH plugin, local MCP bridge, and related application add-on published by the **nextra-studio** GitHub account. It applies whether you use the integration through Claude, OpenAI products, or another compatible AI client. The integration works with Rhino and Grasshopper on your computer. It does not require a Nextra online account.

## Information processed

The plugin processes the tool inputs and application information needed for the requested task. Depending on the tools you use, this can include model geometry and properties, calculations and results, object or layer names, file paths, scripts, application status or dialog text, screenshots, saved preferences, and workflows.

The plugin is not designed to collect identity information such as names, email addresses, or postal addresses. However, information in your models, file paths, screenshots, scripts, or saved notes can contain personal or confidential information. The plugin does not automatically detect or remove that information before returning a tool result. Avoid including information that you do not want your AI client to receive.

## Local processing and disclosure to your AI client

The MCP bridge communicates with the Rhino plug-in over the local loopback address (127.0.0.1), using a locally generated authentication token. Local connection files may contain the token, process information, and model file names or paths. Do not publish these files.

Tool results, including requested model information, images, and saved memory content, are returned to the AI client you connected. If that client uses a cloud service, it may transmit those results to its provider. The provider's terms, privacy policy, account settings, and retention rules govern its handling of that information. Local execution does not mean that all information stays on your computer.

The standard plugin does not send model contents, tool inputs, or tool results to a Nextra-operated server. It contains no built-in advertising or analytics telemetry and does not sell user data. Scripts you ask the integration to run, separately installed application plugins, and any services you configure can have their own file or network access; their behavior is outside this standard plugin's data flow.

## Local storage and retention

Configuration, user preferences, workflows, saved scripts, memory files, and diagnostic logs can be stored under `%APPDATA%\NextraRhinoGh` or in folders you configure. Generated images and files may be stored in temporary, output, or user-selected folders. Saved memory and diagnostic files can include task content, paths, error details, or text you provide. Some edits to saved files can leave backup copies.

There is no single automatic deletion period for these local files. Preferences and workflows are designed to persist between sessions, and plugin updates do not necessarily remove them. Your application, operating system, backups, or cloud-synced folders may retain additional copies. The publisher does not operate a server-side store of your plugin task data.

## Your choices and deletion

You can review the open model and tool requests, limit the information you expose, disable or disconnect the integration in your AI client, and inspect or remove its local files. Close the application and stop the bridge before manually deleting configuration, memory, logs, images, or backup files. Back up anything you want to keep first. Uninstalling the plugin may leave these files behind. Removing the local connection files can require restarting or reconfiguring the integration.

Deletion of conversations or provider-held tool results must be managed through your AI provider. Removing local files does not delete provider-held copies or your own operating-system and cloud backups.

## Support and information you choose to share

If you contact the publisher or open a GitHub issue, the publisher receives the information you choose to include. GitHub issues are public. Do not attach confidential models, personal information, local authentication tokens, or unredacted logs. GitHub and any other support service handle your submission under their own policies. Information you submit for support is separate from the plugin's standard local operation.

## Intended audience and updates

This integration is intended for adult users working with modelling and engineering software. It is not directed to children or users under 18. Changes to the standard plugin's data practices will be described in an updated policy in this repository, with an updated effective date.

## Contact

For privacy questions or requests concerning information you voluntarily shared with the publisher, email [contact@nextra.studio](mailto:contact@nextra.studio). You can also use the [repository's GitHub issues](https://github.com/nextra-studio/nextra-rhinogh/issues) without posting sensitive information. A local file stored only on your computer must be managed on that computer; the publisher cannot access or delete it remotely through this plugin.
