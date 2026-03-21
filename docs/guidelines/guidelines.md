**[<- Back to Overview](../overview.md)**

# Guidelines

- [Guidelines](#guidelines)
  - [1. Repository Structure](#1-repository-structure)
  - [2. Project Structure](#2-project-structure)
    - [a. Libraries `./libraries`](#a-libraries-libraries)
  - [3. Naming Convention](#3-naming-convention)
  - [4. Code Patterns](#4-code-patterns)





## 1. Repository Structure

## 2. Project Structure

### a. Libraries `./libraries`


```


```


## 3. Naming Convention

- All Markdown files MUST be in Uppercase Snake casing

## 4. Code Patterns

- Avoid `ThrowHelper` and `ThrowHelpers` types
- Prefer direct `throw` statements or framework guard APIs for local guard clauses
- If reusable throw behavior is needed, use a .NET 10 extension type method in `Extensions/` instead of a helper class
