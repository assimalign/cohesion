# Cohesion


## Statuses
[![Cohesion SDK](https://github.com/assimalign/cohesion/actions/workflows/sdk.yml/badge.svg?branch=development)](https://github.com/assimalign/cohesion/actions/workflows/sdk.yml) </br>

### Services
[![Core](https://github.com/assimalign/cohesion/actions/workflows/core.yml/badge.svg?branch=development)](https://github.com/assimalign/cohesion/actions/workflows/core.yml) </br>
[![Configuration Store](https://github.com/assimalign/cohesion/actions/workflows/configuration-store.yml/badge.svg?branch=development)](https://github.com/assimalign/cohesion/actions/workflows/configuration-store.yml) </br>
[![API Manager](https://github.com/assimalign/cohesion/actions/workflows/api-manager.yml/badge.svg?branch=development)](ttps://github.com/assimalign/cohesion/actions/workflows/api-manager.yml) </br>
[![Database](https://github.com/assimalign/cohesion/actions/workflows/database.yml/badge.svg?branch=development)](https://github.com/assimalign/cohesion/actions/workflows/database.yml) </br>
[![Dns](https://github.com/assimalign/cohesion/actions/workflows/dns.yml/badge.svg?branch=development)](https://github.com/assimalign/cohesion/actions/workflows/dns.yml) </br>
[![Event Hub](https://github.com/assimalign/cohesion/actions/workflows/event-hub.yml/badge.svg?branch=development)](https://github.com/assimalign/cohesion/actions/workflows/event-hub.yml) </br>
[![Identity](https://github.com/assimalign/cohesion/actions/workflows/identity.yml/badge.svg?branch=development)](https://github.com/assimalign/cohesion/actions/workflows/identity.yml) </br>
[![IoT Hub](https://github.com/assimalign/cohesion/actions/workflows/iot-hub.yml/badge.svg?branch=development)](https://github.com/assimalign/cohesion/actions/workflows/iot-hub.yml) </br>
[![Load Balancer](https://github.com/assimalign/cohesion/actions/workflows/load-balancer.yml/badge.svg?branch=development)](https://github.com/assimalign/cohesion/actions/workflows/load-balancer.yml) </br>
[![Log Space](https://github.com/assimalign/cohesion/actions/workflows/log-space.yml/badge.svg?branch=development)](https://github.com/assimalign/cohesion/actions/workflows/log-space.yml) </br>
[![Media Hub](https://github.com/assimalign/cohesion/actions/workflows/media-hub.yml/badge.svg?branch=development)](https://github.com/assimalign/cohesion/actions/workflows/media-hub.yml) </br>
[![Message Hub](https://github.com/assimalign/cohesion/actions/workflows/message-hub.yml/badge.svg?branch=development)](https://github.com/assimalign/cohesion/actions/workflows/message-hub.yml) </br>
[![NAT Gateway](https://github.com/assimalign/cohesion/actions/workflows/nat-gateway.yml/badge.svg?branch=development)](https://github.com/assimalign/cohesion/actions/workflows/nat-gateway.yml) </br>
[![Notification Hub](https://github.com/assimalign/cohesion/actions/workflows/notification-hub.yml/badge.svg?branch=development)](https://github.com/assimalign/cohesion/actions/workflows/notification-hub.yml) </br>
[![OpenTelemetry](https://github.com/assimalign/cohesion/actions/workflows/opentelemetry.yml/badge.svg?branch=development)](https://github.com/assimalign/cohesion/actions/workflows/opentelemetry.yml) </br>
[![Secret Store](https://github.com/assimalign/cohesion/actions/workflows/secret-store.yml/badge.svg?branch=development)](https://github.com/assimalign/cohesion/actions/workflows/secret-store.yml) </br>
[![Web](https://github.com/assimalign/cohesion/actions/workflows/web.yml/badge.svg?branch=development)](https://github.com/assimalign/cohesion/actions/workflows/web.yml) </br>


### Extensions


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
