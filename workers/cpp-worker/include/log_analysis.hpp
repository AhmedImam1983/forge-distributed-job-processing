#pragma once

#include <nlohmann/json.hpp>
#include <string>

namespace forge::worker {

[[nodiscard]] nlohmann::json analyze_log_text(const std::string& text);

}
