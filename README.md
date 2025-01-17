# Cohesion



[![assimalign.cohesion.core.build](https://github.com/assimalign/cohesion/actions/workflows/assimalign.cohesion.core.build.yml/badge.svg?branch=development)](https://github.com/assimalign/cohesion/actions/workflows/assimalign.cohesion.core.build.yml)

# Repository Structure

Cohesion is a mono repository that contains all the source code, extensions, and tooling in one. This allows for easier development. When working with cohesion it's best to scope development to specific areas of the repository 

The following l

| Folder         | Usage                                                                 |
| -------------- | --------------------------------------------------------------------- |
| `./build`      | This contains all the scripts and process for packaging the SDK which |
| `./.docs`      |                                                                       |
| `./extensions` |                                                                       |
| `./libraries`  | All the source code for cohesion lives in the following folder.       |
| `./modules`    |                                                                       |
| `./samples`    |                                                                       |
| `./sdk`        | This contains source code for MSBuild SDK Style Project.              |
| `./tooling`    |                                                                       |



### SDK Breakdown


|     | Category    | Sub Layer 1 | Sub Layer 2 | Assembly                        | Plaine |     |     |
| --- | ----------- | ----------- | ----------- | ------------------------------- | ------ | --- | --- |
|     | Networking  | DNS         |             |                                 |        |     |     |
|     |             | HTTP        |             | Assimalign.Cohesion.Http        |        |     |     |
|     |             | WebSockets  |             |                                 |        |     |     |
|     | Security    | Identity    |             | Assimalign.Cohesion.Identity    |        |     |     |
|     |             |             |             |                                 |        |     |     |
|     |             |             |             |                                 |        |     |     |
|     |             |             |             |                                 |        |     |     |
|     | Database    |             |             |                                 |        |     |     |
|     |             |             |             |                                 |        |     |     |
|     |             |             |             |                                 |        |     |     |
|     |             |             |             |                                 |        |     |     |
|     | Integration | ConfigStore |             | Assimalign.Cohesion.ConfigStore |        |     |     |
|     |             |             |             |                                 |        |     |     |
|     |             |             |             |                                 |        |     |     |
|     |             |             |             |                                 |        |     |     |
|     | Application | OGraph      |             |                                 |        |     |     |
|     |             | WebApi      |             |                                 |        |     |     |
