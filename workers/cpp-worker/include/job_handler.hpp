#pragma once

#include <nlohmann/json.hpp>
#include <string>

namespace forge::worker {

struct JobHandlerResult {
    bool succeeded = false;
    nlohmann::json result_descriptor = nlohmann::json::object();
    std::string failure_code;
    std::string failure_message;
};

[[nodiscard]] JobHandlerResult handle_job(
    const nlohmann::json& execution_descriptor);

}
