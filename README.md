# Appointment

An intelligent IDE assistant designed to speed up the development and refactoring process. The project bridges the gap between getting advice from the language model and manually applying it by automating code edits based on the context of the entire solution.

## Main features

- **Bidirectional interface**: Support for two modes of operation - "Ask" for project advice and "Edit" for making directive edits to the source code.
- **Context-sensitive editing**: Changes are made directly to the solution files. The system finds target code blocks by normalizing line breaks and minor formatting discrepancies, which guarantees an exact match.
- **Microservice for semantic code search**: Receives a list of files and a search query as input, converts all this into vector representations (embeddings) and finds the 5 most relevant code fragments by meaning, rather than by exact text match. The interaction takes place through standard input/output streams in the Base64 format.
- **Safe Rollback (Revert)**: Before applying each edit, a full backup copy of the file (`OriginalFileContent`) is saved. The user can completely undo any change made with a single click of a button, which eliminates the risk of irreversible damage to the codebase.
- **Navigation through changes**: All applied diffs are displayed in a specialized view with the name of the modified file. Clicking on the file name opens it in the Visual Studio editor.
- **Integration with WebView2**: Interaction with the web interface of the DeepSeek language model is implemented through the implementation of JavaScript scripts. This emulates filling out a form and sending requests, and also allows you to change the AI provider in the future without rebuilding the extension architecture.

## Technical stack and architecture

**Platform**: .NET (WPF, Visual Studio SDK)

**Environment**: Visual Studio Extension (VSIX)

### Key components

- `AiAssistantWindowControl` – the main chat assistant window implemented in XAML.
- `ChangeApplier` – an engine for applying changes that includes algorithms for searching code with whitespace normalization and calculating replacement positions.
- `FileDiff` – a data model for storing the status of a modified file (original content, path, return flag).
- `EmbeddingService` – a microservice for semantic code search based on the ElBruno library.

### AI integration method

DOM manipulation via JavaScript in WebView2 control to interact with the DeepSeek web client.
