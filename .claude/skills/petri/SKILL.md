```markdown
# petri Development Patterns

> Auto-generated skill from repository analysis

## Overview
This skill teaches the core development patterns and conventions used in the `petri` repository, a C# codebase with a focus on consistent file organization, import/export styles, and lightweight commit messaging. While no specific framework is detected, the repository follows clear conventions for file naming, code structure, and testing. This guide will help you contribute effectively to the project by following its established practices.

## Coding Conventions

### File Naming
- Use **PascalCase** for all file names.
  - **Example:** `PetriNet.cs`, `TokenManager.cs`

### Import Style
- Use **relative imports** for referencing other files or namespaces within the project.
  - **Example:**
    ```csharp
    using Petri.Models;
    using Petri.Utils;
    ```

### Export Style
- Use **named exports** for classes, structs, and other types.
  - **Example:**
    ```csharp
    public class PetriNet
    {
        // Implementation
    }
    ```

### Commit Messages
- Freeform style, no strict prefixes, with an average length of 47 characters.
  - **Example:**  
    ```
    Fix token initialization in PetriNet constructor
    ```

## Workflows

### Adding a New Feature
**Trigger:** When implementing a new capability or module  
**Command:** `/add-feature`

1. Create a new file using PascalCase (e.g., `NewFeature.cs`).
2. Implement the feature using named exports (public classes).
3. Use relative imports to reference existing code.
4. Write corresponding tests in a file matching `*.test.*`.
5. Commit your changes with a concise, descriptive message.

### Fixing a Bug
**Trigger:** When resolving a defect or issue  
**Command:** `/fix-bug`

1. Locate the relevant file(s) using PascalCase naming.
2. Apply the fix, maintaining the code style and import conventions.
3. Update or add tests in `*.test.*` files to cover the fix.
4. Commit with a clear message describing the fix.

### Writing Tests
**Trigger:** When adding or updating tests for code  
**Command:** `/write-test`

1. Create or update a test file matching the `*.test.*` pattern (e.g., `PetriNet.test.cs`).
2. Write tests using the project's preferred (unknown) testing framework.
3. Use relative imports to reference the code under test.
4. Run the tests to ensure correctness.
5. Commit with a message indicating the test changes.

## Testing Patterns

- **Test File Pattern:** Name test files using the `*.test.*` convention (e.g., `PetriNet.test.cs`).
- **Testing Framework:** Not explicitly specified; follow existing patterns in the repository.
- **Test Structure:** Place tests alongside or near the code they test, using relative imports.

**Example:**
```csharp
// PetriNet.test.cs
using Petri.Models;
using Xunit; // If using xUnit

public class PetriNetTests
{
    [Fact]
    public void InitializesWithZeroTokens()
    {
        var net = new PetriNet();
        Assert.Equal(0, net.TokenCount);
    }
}
```

## Commands
| Command      | Purpose                                 |
|--------------|-----------------------------------------|
| /add-feature | Start the workflow for adding a feature |
| /fix-bug     | Begin the bugfix workflow               |
| /write-test  | Guide for writing or updating tests     |
```
